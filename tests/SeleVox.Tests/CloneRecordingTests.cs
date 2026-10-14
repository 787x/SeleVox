using NAudio.Wave;
using SeleVox.App;

namespace SeleVox.Tests;

public sealed class CloneRecordingTests
{
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task DefaultMicrophoneRecordsStopsValidatesAndDiscardsOnLifetimeCancellation()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var audio = await Task.Run(() => CloneRecording.RecordAsync(stop.Token, default));
        using var wave = new WaveFileReader(new MemoryStream(audio.Bytes, false)); Assert.InRange(wave.TotalTime.TotalSeconds, 5, 9); CloneAudio.Validate(audio.Bytes, ".wav");
        using var early = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<SeleVox.Core.UserFeedbackException>(() => Task.Run(() => CloneRecording.RecordAsync(early.Token, default)));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => CloneRecording.RecordAsync(default, lifetime.Token)));
    }
}
