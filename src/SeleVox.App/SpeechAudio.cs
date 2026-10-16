using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SeleVox.Core;
using Windows.Media.SpeechSynthesis;
using NAudio.CoreAudioApi;

namespace SeleVox.App;

internal sealed record VoiceChoice(string Id, string Label);

internal sealed class SpeechAudio : ISpeechSynthesis, IAudioPlaybackFactory, IStreamingSpeechSynthesis, IStreamingPlaybackFactory
{
    // Local checks only: reject an OCR automatic read before changing playback ownership.
    internal static void PrecheckAutomatic(string text, Settings settings)
    {
        if (!settings.AllowsAutomaticProvider)
            throw new UserFeedbackException("OCR 自动朗读未获当前在线 TTS 上传授权，未发送。请在设置中对应 TTS Provider 的自动上传选项授权。");
        if (!settings.IsValid || string.IsNullOrWhiteSpace(text) || text.Length > HttpSpeech.MaxTextLength ||
            text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new UserFeedbackException("OCR 自动朗读的文字或已保存设置无效，请检查后手动重试。");
        void Key(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 1280 || key.Any(char.IsControl))
                throw new UserFeedbackException("当前 TTS 凭据缺失或无效，请到对应 Provider 保存 Key。");
        }
        switch (settings.Provider)
        {
            case TtsProvider.Windows:
                var voices = GetVoices();
                if (voices.Count == 0 || settings.VoiceId is not null && !voices.Any(v => v.Id == settings.VoiceId))
                    throw new UserFeedbackException("已保存的 Windows 语音不可用，请在设置中重新选择语音。");
                break;
            case TtsProvider.Azure:
                if (!Settings.IsRegionValid(settings.AzureRegion) || string.IsNullOrWhiteSpace(settings.AzureVoice))
                    throw new UserFeedbackException("请先保存有效的 Azure Region 和 Voice。");
                Key(new AzureCredentials().Read()); break;
            case TtsProvider.Edge:
                if (string.IsNullOrWhiteSpace(settings.EdgeVoice)) throw new UserFeedbackException("请先保存 Edge Voice。");
                break;
            case TtsProvider.MiMo:
                MiMoSpeech.GetStyle(settings);
                var parts = MiMoSpeech.SplitCreativeText(text, settings);
                MiMoSpeech.CreateCreativeRequest(MiMoSpeech.CreativePrefix(settings) + parts[0], settings, MiMoSpeech.GetStyle(settings));
                Key(AzureCredentials.MiMo().Read()); break;
            case TtsProvider.OpenAiCompatible:
                if (settings.OpenAiModel is "tts-1" or "tts-1-hd" && !string.IsNullOrWhiteSpace(settings.OpenAiInstructions))
                    throw new UserFeedbackException("当前 TTS 模型不支持 Instructions，请清空后保存。");
                Key(AzureCredentials.OpenAi(settings.OpenAiBaseUrl).Read()); break;
        }
        settings = VirtualMicrophones.Resolve(settings);
        using var enumerator = new MMDeviceEnumerator();
        var available = false;
        foreach (var id in settings.OutputDeviceIds is { Length: > 0 } ids ? ids : [Settings.DefaultOutput])
        {
            try
            {
                using var device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                available |= device.DataFlow == DataFlow.Render && device.State == DeviceState.Active && !settings.UnavailableOutputIds.Contains(device.ID);
            }
            catch { /* Preserve the existing physical-output fallback policy. */ }
        }
        if (!available && VirtualMicrophones.CanFallback(settings))
        {
            try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); available = device.State == DeviceState.Active; }
            catch { }
        }
        if (!available) throw new UserFeedbackException("已保存输出设备不可用，请在设置中检查输出设备。");
    }
    public bool UsesStreaming(Settings settings) => settings.UsesMiMoStreaming;
    public Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation) =>
        new MiMoSpeech().SynthesizeStreamingAsync(text, settings, buffer, cancellation);
    public IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings) => AudioOutputs.CreateStreamingPlayback(buffer, settings);
    public static IReadOnlyList<VoiceChoice> GetVoices() => SpeechSynthesizer.AllVoices
        .Select(v => new VoiceChoice(v.Id, $"{v.DisplayName} ({v.Language})")).ToList();

    public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
        if (settings.Provider == TtsProvider.MiMo)
            return await new MiMoSpeech().SynthesizeAsync(text, settings, cancellation).ConfigureAwait(false);
        if (settings.Provider == TtsProvider.OpenAiCompatible)
            return await new OpenAiSpeech().SynthesizeAsync(text, settings, cancellation).ConfigureAwait(false);
        if (settings.Provider == TtsProvider.Edge)
            return await new EdgeSpeech().SynthesizeAsync(text, settings, cancellation).ConfigureAwait(false);
        if (settings.Provider == TtsProvider.Azure)
            return await new AzureSpeech().SynthesizeAsync(text, settings, cancellation).ConfigureAwait(false);
        using var synthesizer = new SpeechSynthesizer();
        var voices = SpeechSynthesizer.AllVoices;
        if (voices.Count == 0) throw new UserFeedbackException("没有可用的 Windows 本地语音，请在系统语言设置中安装语音。");
        if (settings.VoiceId is not null)
        {
            var voice = voices.FirstOrDefault(v => v.Id == settings.VoiceId);
            if (voice is null) throw new UserFeedbackException("所选语音已不可用，请在设置中重新选择。");
            synthesizer.Voice = voice;
        }
        synthesizer.Options.SpeakingRate = settings.Rate;
        using var result = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellation).ConfigureAwait(false);
        // WinRT produces a container, not raw PCM. Copy then parse the RIFF/WAVE header.
        using var input = result.AsStreamForRead();
        var copy = new MemoryStream();
        try
        {
            await input.CopyToAsync(copy, cancellation).ConfigureAwait(false);
            copy.Position = 0;
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    public IAudioPlayback CreatePlayback(Stream audio, Settings settings) => AudioOutputs.CreatePlayback(audio, settings);
}

internal sealed record OutputChoice(string Id, string Label);

internal static partial class AudioOutputs
{
    public static IReadOnlyList<OutputChoice> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var choices = new List<OutputChoice> { new(Settings.DefaultOutput, "系统默认输出（每次朗读开始时解析）") };
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device) choices.Add(new(device.ID, device.FriendlyName));
        }
        return choices;
    }

    public static IAudioPlayback CreatePlayback(Stream audio, Settings settings)
    {
        try { settings = VirtualMicrophones.Resolve(settings); }
        catch { audio.Dispose(); throw; }
        var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
        using var enumerator = new MMDeviceEnumerator();
        return CreatePlayback(audio, settings, (id, stream, volume) =>
        {
            MMDevice? device = null;
            try
            {
                device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active) throw new IOException();
                if (settings.UnavailableOutputIds.Contains(device.ID)) throw new IOException();
                if (resolvedIds.Contains(device.ID)) { device.Dispose(); stream.Dispose(); return null; }
                var resolved = device.ID;
                var owned = device; device = null;
                var player = new WasapiPlayback(stream, volume, owned, settings.StartupSilenceMilliseconds);
                resolvedIds.Add(resolved);
                return VirtualMicrophones.Track(player, resolved, settings);
            }
            catch { device?.Dispose(); throw; }
        });
    }

    internal static IAudioPlayback CreatePlayback(Stream audio, Settings settings, Func<string, Stream, float, IAudioPlayback?> open)
    {
        using (audio)
        {
            using var copy = new MemoryStream(); audio.CopyTo(copy);
            var bytes = copy.ToArray();
            using (var validation = new WaveFileReader(new MemoryStream(bytes, writable: false)))
            {
                if (validation.WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) || validation.Length == 0)
                    throw new UserFeedbackException("语音未产生可播放的 PCM/WAVE 音频，请检查文本语言和语音。");
            }
            var players = new List<IAudioPlayback>();
            var failed = 0;
            void Open(string id)
            {
                var stream = new MemoryStream(bytes, writable: false);
                try
                {
                    if (settings.UnavailableOutputIds.Contains(id)) throw new IOException();
                    // MemoryStream has its own cursor, while all outputs share an immutable byte array.
                    var player = open(id, stream, settings.Volume);
                    if (player is not null) players.Add(player);
                    else stream.Dispose();
                }
                catch { stream.Dispose(); ++failed; VirtualMicrophones.MarkFailure(id, settings); }
            }
            foreach (var id in (settings.OutputDeviceIds is { Length: > 0 } ids ? ids : [Settings.DefaultOutput]).Distinct()) Open(id);
            if (settings.StrictOutputDevices && failed > 0)
            {
                foreach (var player in players) { try { player.Dispose(); } catch { /* Release every remaining output too. */ } }
                throw new UserFeedbackException("指定输出设备不可用，未回退播放。");
            }
            string? warning = failed > 0 ? "部分所选输出不可用，已跳过" + (settings.NoFallbackOutputIds.Length > 0 ? "（虚拟麦克风目标不会回退）" : "") : null;
            if (players.Count == 0 && !settings.StrictOutputDevices && VirtualMicrophones.CanFallback(settings))
            {
                Open(Settings.DefaultOutput);
                warning = "所选输出均不可用，已回退到系统默认输出";
            }
            if (players.Count == 0) throw new UserFeedbackException(settings.StrictOutputDevices || !VirtualMicrophones.CanFallback(settings)
                ? "所选输出设备不可用，未向其他设备回退。请检查设备连接。" : "所选设备及系统默认输出均不可用，请检查音频设备连接。");
            return new MultiOutputPlayback(players, warning);
        }
    }
}

internal sealed class WasapiPlayback : IAudioPlayback
{
    private readonly Stream audio;
    private readonly WaveFileReader reader;
    private readonly WasapiPlayer output;
    private readonly MMDevice device;
    private bool disposed;
    internal long SourcePosition => reader.Position;
    public event Action<Exception?>? Finished;

    internal string DeviceId => device.ID;
    public WasapiPlayback(Stream audio, float volume, MMDevice? device = null, int startupSilenceMilliseconds = StartupSilenceProvider.DefaultMilliseconds)
    {
        this.audio = audio;
        WaveFileReader? openedReader = null;
        WasapiPlayer? openedOutput = null;
        try
        {
            if (device is null)
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            this.device = device;
            openedReader = new WaveFileReader(audio);
            if (openedReader.WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
                throw new UserFeedbackException("语音返回了不支持的 WAVE 编码。");
            if (openedReader.Length == 0) throw new UserFeedbackException("语音未产生音频，请选择支持该文本语言的语音。");
            // NAudio 3.1 standard shared mode uses WASAPI AutoConvertPcm and
            // SrcDefaultQuality: each endpoint engine resamples/maps its own format.
            openedOutput = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(80).Build();
            var samples = new VolumeSampleProvider(new StartupSilenceProvider(openedReader.ToSampleProvider(), startupSilenceMilliseconds)) { Volume = volume };
            openedOutput.Init(samples.ToWaveProvider());
            reader = openedReader;
            output = openedOutput;
            output.PlaybackStopped += OnStopped;
        }
        catch
        {
            // Attempt every release even when the endpoint disappeared during Init.
            try { openedOutput?.Dispose(); } catch { }
            try { openedReader?.Dispose(); } catch { }
            try { audio.Dispose(); } finally { device?.Dispose(); }
            throw;
        }
    }
    private void OnStopped(object? sender, StoppedEventArgs args) => Finished?.Invoke(args.Exception);
    public void Play() => output.Play();
    public void Pause() => output.Pause();
    public void Stop() => output.Stop();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        output.PlaybackStopped -= OnStopped;
        try { output.Dispose(); }
        finally { try { reader.Dispose(); } finally { try { audio.Dispose(); } finally { device.Dispose(); } } }
    }
}
