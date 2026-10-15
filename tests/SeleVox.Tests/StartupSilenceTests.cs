using NAudio.Wave;
using SeleVox.App;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class StartupSilenceTests
{
    [Theory]
    [InlineData(24000, 1, 0)] [InlineData(24000, 1, 100)] [InlineData(44100, 2, 150)] [InlineData(48000, 6, 250)]
    public void PrefixIsFrameAlignedAndSourceSamplesRemainExactAcrossReads(int rate, int channels, int milliseconds)
    {
        var source = new Source(rate, channels);
        var provider = new StartupSilenceProvider(source, milliseconds);
        Assert.Same(source.WaveFormat, provider.WaveFormat);
        var prefix = (long)rate * milliseconds / 1000 * channels;
        var actual = new List<float>(); var readSizes = new[] { 1, 3, 71, 512, 2048 }; var turn = 0;
        while (true)
        {
            var block = Enumerable.Repeat(float.NaN, readSizes[turn++ % readSizes.Length]).ToArray();
            var before = source.Position; var count = provider.Read(block);
            if (actual.Count + count <= prefix) Assert.Equal(before, source.Position);
            actual.AddRange(block[..count]);
            if (count == 0) break;
        }
        Assert.All(actual.Take((int)prefix), sample => Assert.Equal(0, sample));
        Assert.Equal(source.Samples, actual.Skip((int)prefix));
        Assert.Equal(source.Samples.Length, source.Position);
        // Resume uses the same provider: it has no reset path or repeated prefix.
        Assert.Equal(0, provider.Read(new float[512]));
    }

    [Fact]
    public void EmptyReadDoesNotConsumeAndInvalidDurationIsRejected()
    {
        var source = new Source(24000, 1); var provider = new StartupSilenceProvider(source, 100);
        Assert.Equal(0, provider.Read(Span<float>.Empty)); Assert.Equal(0, source.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StartupSilenceProvider(source, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StartupSilenceProvider(source, 251));
    }

    private sealed class Source(int rate, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public float[] Samples { get; } = Enumerable.Range(1, 6000).Select(i => (i % 23 - 11) / 16f).ToArray();
        public int Position;
        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, Samples.Length - Position);
            Samples.AsSpan(Position, count).CopyTo(buffer); Position += count; return count;
        }
    }
}
