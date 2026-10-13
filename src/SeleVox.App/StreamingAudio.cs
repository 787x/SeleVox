using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SeleVox.Core;

namespace SeleVox.App;

internal static partial class AudioOutputs
{
    public static IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        using var enumerator = new MMDeviceEnumerator();
        return CreateStreamingPlayback(buffer, settings, (id, reader, volume) =>
        {
            MMDevice? device = null;
            try
            {
                device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active) throw new IOException();
                if (resolved.Contains(device.ID)) { device.Dispose(); reader.Dispose(); return null; }
                var actual = device.ID; var owned = device; device = null;
                var playback = new StreamingWasapiPlayback(reader, volume, owned); resolved.Add(actual); return playback;
            }
            catch { device?.Dispose(); throw; }
        });
    }
    internal static IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings,
        Func<string, StreamingPcmBuffer.Reader, float, IAudioPlayback?> open)
    {
        var players = new List<IAudioPlayback>(); var failed = 0;
        void Open(string id)
        {
            var reader = buffer.CreateReader();
            try { var player = open(id, reader, settings.Volume); if (player is null) reader.Dispose(); else players.Add(player); }
            catch { reader.Dispose(); ++failed; }
        }
        try
        {
            foreach (var id in (settings.OutputDeviceIds is { Length: > 0 } ids ? ids : [Settings.DefaultOutput]).Distinct()) Open(id);
            string? warning = failed > 0 ? "部分所选流式输出不可用，已跳过" : null;
            if (players.Count == 0) { Open(Settings.DefaultOutput); warning = "所选流式输出均不可用，已回退到系统默认输出"; }
            if (players.Count == 0) throw new UserFeedbackException("所选设备及系统默认输出均不可用，请检查音频设备连接。");
            return new MultiOutputPlayback(players, warning);
        }
        catch { foreach (var player in players) { try { player.Dispose(); } catch { } } throw; }
    }
}

internal sealed class StreamingWasapiPlayback : IAudioPlayback
{
    private readonly StreamingPcmBuffer.Reader reader;
    private readonly MMDevice device;
    private readonly WasapiPlayer output;
    private bool disposed;
    public event Action<Exception?>? Finished;
    internal long SourcePosition => reader.ConsumedBytes;
    internal long SilenceBytes => reader.SilenceBytes;
    public StreamingWasapiPlayback(StreamingPcmBuffer.Reader reader, float volume, MMDevice device, int startupSilenceMilliseconds = StartupSilenceProvider.DefaultMilliseconds)
    {
        this.reader = reader; this.device = device;
        WasapiPlayer? opened = null;
        try
        {
            opened = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(80).Build();
            var samples = new VolumeSampleProvider(new StartupSilenceProvider(new PcmProvider(reader).ToSampleProvider(), startupSilenceMilliseconds)) { Volume = volume };
            opened.Init(samples.ToWaveProvider()); output = opened; output.PlaybackStopped += OnStopped;
        }
        catch { try { opened?.Dispose(); } finally { try { reader.Dispose(); } finally { device.Dispose(); } } throw; }
    }
    private void OnStopped(object? sender, StoppedEventArgs args) => Finished?.Invoke(args.Exception ??
        (reader.IsDrained ? null : new UserFeedbackException("流式输出在音频完成前停止。")));
    public void Play() => output.Play();
    public void Pause() => output.Pause();
    public void Stop() => output.Stop();
    public void Dispose()
    {
        if (disposed) return; disposed = true; output.PlaybackStopped -= OnStopped;
        try { output.Dispose(); } finally { try { reader.Dispose(); } finally { device.Dispose(); } }
    }
    private sealed class PcmProvider(StreamingPcmBuffer.Reader reader) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(24000, 16, 1);
        public int Read(Span<byte> buffer) => reader.Read(buffer);
    }
}
