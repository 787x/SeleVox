using NAudio.Wave;

namespace SeleVox.App;

// Each output owns one instance for the lifetime of its playback session.
// Silence is sent to the endpoint before reading any effective source samples.
internal sealed class StartupSilenceProvider : ISampleProvider
{
    // 100ms was the smallest tested prefix that avoided startup attenuation in
    // repeated endpoint loopback checks. This is once per session, not per resume.
    internal const int DefaultMilliseconds = 100;
    private readonly ISampleProvider source;
    private long remaining;
    public WaveFormat WaveFormat => source.WaveFormat;

    public StartupSilenceProvider(ISampleProvider source, int milliseconds)
    {
        if (milliseconds < 0 || milliseconds > 250) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        this.source = source;
        remaining = (long)source.WaveFormat.SampleRate * milliseconds / 1000 * source.WaveFormat.Channels;
    }

    public int Read(Span<float> buffer)
    {
        var silence = (int)Math.Min(remaining, buffer.Length);
        buffer[..silence].Clear(); remaining -= silence;
        return silence + (silence < buffer.Length ? source.Read(buffer[silence..]) : 0);
    }
}
