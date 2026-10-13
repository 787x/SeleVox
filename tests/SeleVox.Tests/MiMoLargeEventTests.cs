using System.Text;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class MiMoLargeEventTests
{
    internal static string Event(byte[] pcm) => "data: {\"choices\":[{\"delta\":{\"audio\":{\"data\":\"" + Convert.ToBase64String(pcm) + "\"}}}]}\r\n\r\n";
    [Theory] [InlineData(7)] [InlineData(8191)]
    public async Task LargeFragmentedEventsPreserveEveryPcmByteAndMetrics(int fragment)
    {
        var expected = Enumerable.Range(0, 1800001).Select(i => (byte)i).ToArray();
        var metrics = new MiMoStreamingMetrics();
        using var input = new MiMoStreamingTests.FragmentStream(Encoding.UTF8.GetBytes(Event(expected) + Event([71, 72]) + "data: [DONE]\n\n"), fragment);
        var chunks = new List<byte[]>();
        await foreach (var pcm in MiMoSpeech.ReadSseAudioAsync(input, default, metrics: metrics)) chunks.Add(pcm);
        Assert.Equal(2, chunks.Count); Assert.Equal(expected, chunks[0]); Assert.Equal(new byte[] { 71, 72 }, chunks[1]);
        Assert.InRange(metrics.MaxEventBytes, 2 * 1024 * 1024 + 1, MiMoSpeech.MaxSseEventBytes);
        Assert.NotNull(metrics.FirstCompleteAudioEventMilliseconds); Assert.True(metrics.ParseMilliseconds > 0);
    }
    [Fact] public async Task NearNewLimitSucceedsAndOversizedCommentsAndMultilineDataAreBounded()
    {
        var pcm = new byte[(MiMoSpeech.MaxSseEventBytes - 128) / 4 * 3];
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes(Event(pcm) + "data: [DONE]\n\n"));
        var metrics = new MiMoStreamingMetrics(); var count = 0;
        await foreach (var decoded in MiMoSpeech.ReadSseAudioAsync(valid, default, metrics: metrics)) { Assert.Equal(pcm, decoded); ++count; }
        Assert.Equal(1, count); Assert.InRange(metrics.MaxEventBytes, MiMoSpeech.MaxSseEventBytes - 150, MiMoSpeech.MaxSseEventBytes);
        foreach (var text in new[] { ":" + new string('x', MiMoSpeech.MaxSseEventBytes),
            string.Concat(Enumerable.Repeat("data: \"private\"\n", MiMoSpeech.MaxSseEventBytes / 12)) })
        {
            using var invalid = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var error = await Assert.ThrowsAsync<UserFeedbackException>(async () =>
            { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(invalid, default)) { } });
            Assert.Contains("16 MiB", error.Message); Assert.DoesNotContain("private", error.Message);
        }
    }
    [Fact] public async Task LargeEventDoesNotReleasePartialPcmBeforeItsDelimiterAndCancellationStopsReceive()
    {
        var eventText = Event(new byte[1800000]);
        using var input = new MiMoStreamingTests.DelayedStream(eventText[..^2], eventText[^2..] + "data: [DONE]\n\n");
        using var cancellation = new CancellationTokenSource();
        var yielded = false;
        var pending = Task.Run(async () => { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(input, cancellation.Token)) yielded = true; });
        await Task.Delay(100); Assert.False(yielded); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        input.Release.TrySetResult(); Assert.False(yielded);
    }
    [Fact] public async Task LargePayloadFeedsBoundedRingInOrderWithOddCarryAndCancellableBackpressure()
    {
        var pcm = Enumerable.Range(0, 1800001).Select(i => (byte)i).ToArray();
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(
            new MemoryStream(Encoding.UTF8.GetBytes(Event(pcm) + Event([99]) + "data: [DONE]\n\n"))))));
        using var buffer = new StreamingPcmBuffer(4096, 2); using var reader = buffer.CreateReader();
        var metrics = new MiMoStreamingMetrics(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, cancellation.Token, metrics);
        await buffer.WaitForReadyAsync(cancellation.Token); Assert.False(pending.IsCompleted); Assert.Equal(4096, buffer.PeakBufferedBytes);
        using var output = new MemoryStream(); var block = new byte[2000];
        while (!pending.IsCompleted || reader.ConsumedBytes < buffer.ProducedBytes)
        {
            cancellation.Token.ThrowIfCancellationRequested(); var before = reader.ConsumedBytes; reader.Read(block);
            var consumed = (int)(reader.ConsumedBytes - before); output.Write(block, 0, consumed);
            if (consumed == 0) await Task.Delay(1, cancellation.Token); else await Task.Yield();
        }
        await pending; Assert.Equal(pcm.Concat(new byte[] { 99 }), output.ToArray());
        Assert.Equal(1800002, metrics.AudioBytes); Assert.InRange(buffer.PeakBufferedBytes, 1, 4096);

        using var stoppedBuffer = new StreamingPcmBuffer(4096, 2); using var stoppedReader = stoppedBuffer.CreateReader();
        using var stop = new CancellationTokenSource();
        var stopped = new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), stoppedBuffer, stop.Token);
        await stoppedBuffer.WaitForReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped); var accepted = stoppedBuffer.ProducedBytes;
        await Task.Delay(30); Assert.Equal(accepted, stoppedBuffer.ProducedBytes); Assert.InRange(accepted, 2, 4096);
    }
    [Fact] public async Task TimeoutPhasesAndCancellationAreDistinguished()
    {
        using var first = new MiMoStreamingTests.DelayedStream("", ""); var metrics = new MiMoStreamingMetrics();
        await Assert.ThrowsAsync<UserFeedbackException>(async () =>
        { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(first, default, TimeSpan.FromMilliseconds(30), metrics)) { } });
        Assert.Equal("首次音频接收", metrics.TimeoutStage);
        using var later = new MiMoStreamingTests.DelayedStream(Event([1, 2]), ""); metrics = new();
        await Assert.ThrowsAsync<UserFeedbackException>(async () =>
        { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(later, default, TimeSpan.FromMilliseconds(30), metrics)) { } });
        Assert.Equal("网络空闲", metrics.TimeoutStage);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); metrics = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(later, cancel.Token, metrics: metrics)) { } });
        Assert.Null(metrics.TimeoutStage);
    }
    [Fact] public async Task LargeMalformedJsonAndBase64FailWithoutExposingPayloadAndDeclaredTotalIsBounded()
    {
        foreach (var body in new[] { "data: {\"private\":\"" + new string('a', 2200000) + "\" BROKEN}\n\n",
            "data: {\"choices\":[{\"delta\":{\"audio\":{\"data\":\"" + new string('a', 2200000) + "private!\"}}}]}\n\n" })
        {
            using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(new MemoryStream(Encoding.UTF8.GetBytes(body))))));
            using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
            var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, default));
            Assert.DoesNotContain("private", error.Message); Assert.Equal(0, buffer.ProducedBytes);
        }
        using var tooLarge = new HttpClient(new MiMoStreamingTests.Handler((_, _) =>
        {
            var response = MiMoStreamingTests.Response(new MemoryStream()); response.Content.Headers.ContentLength = HttpSpeech.MaxResponseBytes + 1;
            return Task.FromResult(response);
        }));
        using var emptyBuffer = new StreamingPcmBuffer();
        Assert.Contains("响应过大", (await Assert.ThrowsAsync<UserFeedbackException>(() => new MiMoSpeech(tooLarge, () => "fixture").SynthesizeStreamingAsync("fixture", new(), emptyBuffer, default))).Message);
    }
    [Fact] public async Task LargeMultilineDataAndDecodeCancellationRemainSupported()
    {
        var pcm = new byte[1800000];
        var body = Event(pcm).Replace("\"choices\":[", "\"choices\":\ndata: [") + "data: [DONE]\n\n";
        using var input = new MiMoStreamingTests.FragmentStream(Encoding.UTF8.GetBytes(body), 4093);
        var count = 0;
        await foreach (var decoded in MiMoSpeech.ReadSseAudioAsync(input, default)) { Assert.Equal(pcm, decoded); ++count; }
        Assert.Equal(1, count);
        using var stop = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encoded = new string('A', 16 * 1024 * 1024);
        var decoding = Task.Run(() => { entered.TrySetResult(); return MiMoSpeech.DecodeBase64(encoded, stop.Token); });
        await entered.Task; stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
    }
    [Fact] public async Task WireByteLimitCountsCrLfAndUnknownLengthResponseRemainsBounded()
    {
        var exact = ":" + new string('a', MiMoSpeech.MaxSseEventBytes - 3) + "\r\n\r\ndata: [DONE]\r\n\r\n";
        using var accepted = new MemoryStream(Encoding.UTF8.GetBytes(exact)); var metrics = new MiMoStreamingMetrics();
        await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(accepted, default, metrics: metrics)) Assert.Fail("Comment event has no audio.");
        Assert.Equal(MiMoSpeech.MaxSseEventBytes, metrics.MaxEventBytes);
        using var exceeded = new MemoryStream(Encoding.UTF8.GetBytes(exact.Replace(":aaa", ":aaaa")));
        Assert.Contains("16 MiB", (await Assert.ThrowsAsync<UserFeedbackException>(async () =>
        { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(exceeded, default)) { } })).Message);
        var frame = Encoding.UTF8.GetBytes(":" + new string('a', 1024 * 1024 - 2) + "\n\n");
        using var repeated = new RepeatedFrames(frame, 50);
        Assert.Contains("响应过大", (await Assert.ThrowsAsync<UserFeedbackException>(async () =>
        { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(repeated, default)) { } })).Message);
    }
    private sealed class RepeatedFrames(byte[] frame, int count) : MemoryStream
    {
        private long position;
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (long)frame.Length * count - position;
            var length = (int)Math.Min(destination.Length, remaining); var written = 0;
            while (written < length)
            {
                var offset = (int)(position % frame.Length); var copy = Math.Min(length - written, frame.Length - offset);
                frame.AsSpan(offset, copy).CopyTo(destination.Span[written..]); written += copy; position += copy;
            }
            return ValueTask.FromResult(length);
        }
    }
}
