using System.Text.Json;
using NAudio.Wave;
using SeleVox.App;

namespace SeleVox.Tests;

public sealed class CloneLibraryTests
{
    private static CloneAudio Audio(int seconds = 6) => new(AudioGenerationTests.Wave(frames: 24000 * seconds), ".wav");
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task LibraryCopiesReloadsPreviewsAndDeletesOwnedFiles(int type)
    {
        var folder = Path.Combine(Path.GetTempPath(), "selevox-pr12-" + Guid.NewGuid().ToString("N")); var library = new CloneLibrary(folder);
        var original = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"); await File.WriteAllBytesAsync(original, Audio().Bytes);
        try
        {
            var audio = await CloneAudio.ReadAsync(original, default); var sample = await library.AddAsync("测试音色", (CloneSource)type, audio, default);
            Assert.Equal((CloneSource)type, sample.Source); Assert.Equal(sample, Assert.Single(new CloneLibrary(folder).Load())); Assert.True(sample.Created <= DateTimeOffset.UtcNow);
            Assert.Equal(audio.Bytes, await File.ReadAllBytesAsync(sample.FilePath)); Assert.NotEqual(original, sample.FilePath);
            var read = await CloneAudio.ReadAsync(sample.FilePath, default); Assert.StartsWith("data:audio/wav;base64,", read.DataUri);
            Assert.Equal(audio.Bytes, Convert.FromBase64String(read.DataUri.Split(',')[1]));
            using var wave = new WaveFileReader(read.Preview(default).Open()); Assert.Equal(6, wave.TotalTime.TotalSeconds);
            await library.DeleteAsync(sample, default); Assert.Empty(library.Load()); Assert.False(File.Exists(sample.FilePath)); Assert.True(File.Exists(original));
        }
        finally { File.Delete(original); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory] [InlineData(4)] [InlineData(31)]
    public void DurationRejectedForUploadRecordingAndDesign(int seconds) => Assert.Throws<SeleVox.Core.UserFeedbackException>(() => CloneAudio.Validate(Audio(seconds).Bytes, ".wav"));
    [Theory] [InlineData(5)] [InlineData(30)]
    public void BoundaryDurationsAccepted(int seconds) => CloneAudio.Validate(Audio(seconds).Bytes, ".wav");
    [Fact] public async Task InvalidFormatSizeAndSafeMissingPathFeedback()
    {
        Assert.Throws<SeleVox.Core.UserFeedbackException>(() => CloneAudio.Validate([1, 2, 3], ".wav"));
        Assert.Throws<SeleVox.Core.UserFeedbackException>(() => CloneAudio.Validate([1, 2, 3], ".mp3"));
        Assert.Throws<SeleVox.Core.UserFeedbackException>(() => CloneAudio.Validate(Audio().Bytes, ".flac"));
        Assert.Throws<SeleVox.Core.UserFeedbackException>(() => CloneAudio.Validate(new byte[CloneAudio.MaxFileBytes + 1], ".wav"));
        var ex = await Assert.ThrowsAsync<SeleVox.Core.UserFeedbackException>(() => CloneAudio.ReadAsync("C:/private-secret-nonexistent.wav", default)); Assert.DoesNotContain("private-secret", ex.Message);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try { await using (var f = File.Create(path)) f.SetLength(CloneAudio.MaxFileBytes + 1L);
            await Assert.ThrowsAsync<SeleVox.Core.UserFeedbackException>(() => CloneAudio.ReadAsync(path, default)); }
        finally { File.Delete(path); }
    }
    [Fact] public void Mp3FramesDecodeValidateAndPreviewWithoutNetwork()
    {
        using var fixture = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone.mp3"));
        using var bytes = new MemoryStream(); double seconds = 0; Mp3Frame? frame;
        var frames = new List<Mp3Frame>(); while ((frame = Mp3Frame.LoadFromStream(fixture)) is not null) frames.Add(frame);
        Assert.NotEmpty(frames);
        while (seconds < 6) foreach (var f in frames) { bytes.Write(f.RawData); seconds += (double)f.SampleCount / f.SampleRate; }
        var audio = new CloneAudio(bytes.ToArray(), ".mp3"); CloneAudio.Validate(audio.Bytes, audio.Extension);
        Assert.StartsWith("data:audio/mpeg;base64,", audio.DataUri); using var wave = new WaveFileReader(audio.Preview(default).Open()); Assert.InRange(wave.TotalTime.TotalSeconds, 5, 30);
    }
    [Fact] public async Task CancelInvalidMetadataAndForeignDeletionPreserveAssets()
    {
        var folder = Path.Combine(Path.GetTempPath(), "selevox-pr12-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); var library = new CloneLibrary(folder);
        try
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.AddAsync("cancel", CloneSource.Upload, Audio(), cancel.Token)); Assert.Empty(Directory.GetFiles(folder));
            var sample = await library.AddAsync("keep", CloneSource.VoiceDesign, Audio(), default);
            await Assert.ThrowsAsync<SeleVox.Core.UserFeedbackException>(() => library.DeleteAsync(sample with { FilePath = "C:/private.wav" }, default)); Assert.True(File.Exists(sample.FilePath));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.DeleteAsync(sample, cancel.Token)); Assert.Single(library.Load());
            var index = Path.Combine(folder, "samples.json"); File.WriteAllText(index, "bad-json");
            await Assert.ThrowsAsync<SeleVox.Core.UserFeedbackException>(() => library.AddAsync("new", CloneSource.Upload, Audio(), default)); Assert.Equal("bad-json", File.ReadAllText(index));
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { sample with { FilePath = "C:/private.wav" } })); Assert.Throws<SeleVox.Core.UserFeedbackException>(() => library.Load()); Assert.True(File.Exists(sample.FilePath));
        }
        finally { Directory.Delete(folder, true); }
    }
}
