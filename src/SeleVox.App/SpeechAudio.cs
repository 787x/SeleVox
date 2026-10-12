using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SeleVox.Core;
using Windows.Media.SpeechSynthesis;
using NAudio.CoreAudioApi;

namespace SeleVox.App;

internal sealed record VoiceChoice(string Id, string Label);

internal sealed class SpeechAudio : ISpeechSynthesis, IAudioPlaybackFactory
{
    public static IReadOnlyList<VoiceChoice> GetVoices() => SpeechSynthesizer.AllVoices
        .Select(v => new VoiceChoice(v.Id, $"{v.DisplayName} ({v.Language})")).ToList();

    public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
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

internal static class AudioOutputs
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
        var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
        using var enumerator = new MMDeviceEnumerator();
        return CreatePlayback(audio, settings, (id, stream, volume) =>
        {
            MMDevice? device = null;
            try
            {
                device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active) throw new IOException();
                if (resolvedIds.Contains(device.ID)) { device.Dispose(); stream.Dispose(); return null; }
                var resolved = device.ID;
                var owned = device; device = null;
                var player = new WasapiPlayback(stream, volume, owned);
                resolvedIds.Add(resolved);
                return player;
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
                    // MemoryStream has its own cursor, while all outputs share an immutable byte array.
                    var player = open(id, stream, settings.Volume);
                    if (player is not null) players.Add(player);
                    else stream.Dispose();
                }
                catch { stream.Dispose(); ++failed; }
            }
            foreach (var id in (settings.OutputDeviceIds is { Length: > 0 } ids ? ids : [Settings.DefaultOutput]).Distinct()) Open(id);
            string? warning = failed > 0 ? "部分所选输出不可用，已跳过" : null;
            if (players.Count == 0)
            {
                Open(Settings.DefaultOutput);
                warning = "所选输出均不可用，已回退到系统默认输出";
            }
            if (players.Count == 0) throw new UserFeedbackException("所选设备及系统默认输出均不可用，请检查音频设备连接。");
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
    public WasapiPlayback(Stream audio, float volume, MMDevice? device = null)
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
            var samples = new VolumeSampleProvider(openedReader.ToSampleProvider()) { Volume = volume };
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
