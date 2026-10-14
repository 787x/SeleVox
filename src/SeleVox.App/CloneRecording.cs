using NAudio.Wave;

namespace SeleVox.App;

internal static class CloneRecording
{
    internal static async Task<CloneAudio> RecordAsync(CancellationToken stop, CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(32));
        var format = new WaveFormat(24000, 16, 1);
        using var recorder = new WasapiRecorderBuilder().WithFormat(format).Build();
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), format))
        {
            var remaining = format.AverageBytesPerSecond * 30;
            var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            // NAudio 3.1 CaptureAsync skips WASAPI Silent packets. The event API supplies
            // zeroed packets, preserving real pauses and the reference sample's duration.
            recorder.DataAvailable += (data, _, _, _) =>
            {
                var count = Math.Min(data.Length, remaining); count -= count % format.BlockAlign;
                writer.Write(data[..count]); remaining -= count;
                if (remaining == 0) recorder.StopRecording();
            };
            recorder.RecordingStopped += (_, e) => finished.TrySetResult(e.Exception);
            recorder.StartRecording();
            using var registration = timeout.Token.Register(recorder.StopRecording);
            var error = await finished.Task.ConfigureAwait(false);
            if (error is not null) throw new SeleVox.Core.UserFeedbackException("录音设备发生错误，请检查麦克风连接和权限。");
        }
        lifetime.ThrowIfCancellationRequested();
        var bytes = memory.ToArray(); CloneAudio.Validate(bytes, ".wav", lifetime);
        return new(bytes, ".wav");
    }
}
