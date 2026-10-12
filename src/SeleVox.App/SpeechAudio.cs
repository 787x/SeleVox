using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SeleVox.Core;
using Windows.Media.SpeechSynthesis;

namespace SeleVox.App;

internal sealed record VoiceChoice(string Id, string Label);

internal sealed class SpeechAudio : ISpeechAudio
{
    public static IReadOnlyList<VoiceChoice> GetVoices() => SpeechSynthesizer.AllVoices
        .Select(v => new VoiceChoice(v.Id, $"{v.DisplayName} ({v.Language})")).ToList();

    public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
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

    public IAudioPlayback CreatePlayback(Stream audio, float volume) => new WasapiPlayback(audio, volume);
}

internal sealed class WasapiPlayback : IAudioPlayback
{
    private readonly Stream audio;
    private readonly WaveFileReader reader;
    private readonly WasapiPlayer output;
    private bool disposed;
    internal long SourcePosition => reader.Position;
    public event Action<Exception?>? Finished;

    public WasapiPlayback(Stream audio, float volume)
    {
        this.audio = audio;
        WaveFileReader? openedReader = null;
        WasapiPlayer? openedOutput = null;
        try
        {
            openedReader = new WaveFileReader(audio);
            if (openedReader.WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
                throw new UserFeedbackException("本地语音返回了不支持的 WAVE 编码。");
            openedOutput = new WasapiPlayerBuilder().WithSharedMode().WithLatency(80).Build();
            var samples = new VolumeSampleProvider(openedReader.ToSampleProvider()) { Volume = volume };
            openedOutput.Init(samples.ToWaveProvider());
            reader = openedReader;
            output = openedOutput;
            output.PlaybackStopped += OnStopped;
        }
        catch { openedOutput?.Dispose(); openedReader?.Dispose(); audio.Dispose(); throw; }
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
        finally { try { reader.Dispose(); } finally { audio.Dispose(); } }
    }
}
