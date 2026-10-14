using NAudio.Wave;
using NAudio.Utils;
using SeleVox.Core;
using System.Text.Json;

namespace SeleVox.App;

// No WPF, playback device or credential value is part of the generation operation.
internal sealed class AudioGeneration(ISpeechSynthesis speech)
{
    internal const int MaxBytes = 256 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<GeneratedAudio> GenerateAsync(string text, Settings settings, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await GenerateCoreAsync(text, settings, cancellation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<GeneratedAudio> GenerateCoreAsync(string text, Settings settings, CancellationToken cancellation)
    {
        if (!settings.IsValid) throw new UserFeedbackException("合成配置无效，请检查参数。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > HttpSpeech.MaxTextLength)
            throw new UserFeedbackException($"请输入 1–{HttpSpeech.MaxTextLength} 字符的文本。");
        // Snapshot arrays too: later draft edits cannot relabel a completed result.
        settings = settings with { MiMoOverallStyles = settings.MiMoOverallStyles?.ToArray() };
        using var merged = new MemoryStream();
        WaveFileWriter? writer = null;
        WaveFormat? format = null;
        try
        {
            // Azure's single response can stop at the service duration limit; keep requests short.
            // Windows is also bounded into Unicode-safe chunks. Other providers retain their own splitter.
            var parts = settings.Provider is TtsProvider.Azure or TtsProvider.Windows
                ? HttpSpeech.SplitText(text, 800, false, cancellation) : [text];
            foreach (var part in parts)
            {
                cancellation.ThrowIfCancellationRequested();
                using var stream = await speech.SynthesizeAsync(part, settings, cancellation).ConfigureAwait(false);
                using var copy = new MemoryStream();
                var buffer = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                {
                    if (copy.Length + read > MaxBytes) throw new UserFeedbackException("音频超过 256 MiB 上限，请缩短文本。");
                    copy.Write(buffer, 0, read);
                }
                using var wave = HttpSpeech.ValidatedWave(copy.ToArray(), cancellation);
                if (format is null) { format = wave.WaveFormat; writer = new WaveFileWriter(new IgnoreDisposeStream(merged), format); }
                else if (!format.Equals(wave.WaveFormat)) throw new UserFeedbackException("分段音频格式不一致。");
                if (merged.Length + wave.Length > MaxBytes - 1024) throw new UserFeedbackException("音频超过 256 MiB 上限，请缩短文本。");
                while ((read = wave.Read(buffer, 0, buffer.Length)) != 0)
                { cancellation.ThrowIfCancellationRequested(); writer!.Write(buffer, 0, read); }
            }
            writer?.Dispose(); writer = null;
            cancellation.ThrowIfCancellationRequested();
            return GeneratedAudio.Create(merged.ToArray(), settings, cancellation);
        }
        finally { writer?.Dispose(); }
    }
}

internal sealed class GeneratedAudio
{
    private readonly byte[] bytes;
    internal Settings Configuration { get; }
    internal long SampleFrames { get; }
    internal string Description { get; }
    private GeneratedAudio(byte[] bytes, Settings settings, WaveFileReader wave)
    {
        this.bytes = bytes; Configuration = settings;
        SampleFrames = wave.Length / wave.WaveFormat.BlockAlign;
        var voice = settings.Provider switch { TtsProvider.Windows => settings.VoiceId ?? "系统默认", TtsProvider.Azure => settings.AzureVoice,
            TtsProvider.Edge => settings.EdgeVoice, TtsProvider.MiMo => settings.MiMoVoice, _ => settings.OpenAiVoice };
        var details = settings.Provider switch {
            TtsProvider.Azure => $"Region={settings.AzureRegion}",
            TtsProvider.Edge => $"Pitch={settings.EdgePitchHz} Hz",
            TtsProvider.MiMo => $"Model={settings.MiMoModel} · 完整 WAVE · 风格={settings.MiMoStyle} · 标签={MiMoStyleTags.GetPrefix(settings.MiMoOverallStyles)}",
            TtsProvider.OpenAiCompatible => $"{settings.OpenAiBaseUrl} · Model={settings.OpenAiModel} · Instructions={settings.OpenAiInstructions}", _ => "" };
        Description = $"{settings.Provider} · Voice={voice} · Rate={settings.Rate:0.##} · {details}\n{wave.WaveFormat} · {SampleFrames} 帧 · {wave.TotalTime.TotalSeconds:0.##} 秒 · {bytes.Length:N0} 字节";
    }
    internal static GeneratedAudio Create(byte[] bytes, Settings settings, CancellationToken cancellation = default)
    {
        if (bytes.Length > AudioGeneration.MaxBytes) throw new UserFeedbackException("音频超过大小上限。");
        using var wave = HttpSpeech.ValidatedWave(bytes, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return new GeneratedAudio(bytes, settings, wave);
    }
    internal Stream Open() => new MemoryStream(bytes, writable: false);
    internal async Task SaveAsync(string path, bool overwriteConfirmed, CancellationToken cancellation)
    {
        path = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".selevox-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
            { await output.WriteAsync(bytes, cancellation).ConfigureAwait(false); await output.FlushAsync(cancellation).ConfigureAwait(false); output.Flush(true); }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwriteConfirmed);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

// Separate from settings.json; intentionally omits input text, results, keys and automatic permissions.
internal sealed record WorkbenchPreferences(Settings Tts, double Left = double.NaN, double Top = double.NaN, double Width = 780, double Height = 850)
{
    internal static Settings OnlyTts(Settings s) => new(VoiceId: s.VoiceId, Provider: s.Provider, Rate: s.Rate,
        AzureRegion: s.AzureRegion, AzureVoice: s.AzureVoice, EdgeVoice: s.EdgeVoice, EdgePitchHz: s.EdgePitchHz,
        EdgeLocaleFilter: s.EdgeLocaleFilter, MiMoModel: s.MiMoModel, MiMoVoice: s.MiMoVoice, MiMoStyle: s.MiMoStyle,
        MiMoOverallStyles: s.MiMoOverallStyles?.ToArray(), MiMoStreaming: false,
        OpenAiModel: s.OpenAiModel, OpenAiVoice: s.OpenAiVoice, OpenAiInstructions: s.OpenAiInstructions);
    internal static WorkbenchPreferences Load(string path, Settings fallback)
    {
        try { var p = JsonSerializer.Deserialize<WorkbenchPreferences>(File.ReadAllText(path), new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
            if (p is { Tts.IsValid: true } && double.IsFinite(p.Width) && double.IsFinite(p.Height)) return p; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(OnlyTts(fallback));
    }
    internal void Save(string path)
    {
        var p = this with { Tts = OnlyTts(Tts) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        // Keep the existing Tts JSON shape, but write only workbench fields, never
        // daily settings, automatic permissions or service-address credential bindings.
        var s = p.Tts;
        var persisted = new { Tts = new { s.VoiceId, s.Provider, s.Rate, s.AzureRegion, s.AzureVoice,
            s.EdgeVoice, s.EdgePitchHz, s.EdgeLocaleFilter, s.MiMoModel, s.MiMoVoice, s.MiMoStyle,
            s.MiMoOverallStyles, s.MiMoStreaming, s.OpenAiModel, s.OpenAiVoice, s.OpenAiInstructions },
            p.Left, p.Top, p.Width, p.Height };
        try { File.WriteAllText(temp, JsonSerializer.Serialize(persisted, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
