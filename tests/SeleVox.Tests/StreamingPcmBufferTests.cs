using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class StreamingPcmBufferTests
{
    [Fact] public async Task IndependentReadersConsumeExactlyTheSamePcmAndOnlyFinishAfterComplete()
    {
        using var buffer = new StreamingPcmBuffer(32, 4); using var first = buffer.CreateReader(); using var second = buffer.CreateReader();
        var source = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray(); await buffer.WriteAsync(source, default);
        var a = new byte[10]; Assert.Equal(10, first.Read(a)); Assert.Equal(source[..10], a);
        var b = new byte[24]; Assert.Equal(24, second.Read(b)); Assert.Equal(source, b);
        var tail = new byte[14]; Assert.Equal(14, first.Read(tail)); Assert.Equal(source[10..], tail);
        var silence = Enumerable.Repeat((byte)255, 6).ToArray(); Assert.Equal(6, first.Read(silence)); Assert.All(silence, value => Assert.Equal(0, value));
        Assert.Equal(24, first.ConsumedBytes); Assert.Equal(6, first.SilenceBytes);
        buffer.Complete(); Assert.Equal(0, first.Read(silence)); Assert.Equal(0, second.Read(silence));
    }
    [Fact] public async Task SlowOutputAndPauseApplyBackpressureWithoutLosingPcm()
    {
        using var buffer = new StreamingPcmBuffer(8, 4); using var fast = buffer.CreateReader(); using var slow = buffer.CreateReader();
        await buffer.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, default);
        var writing = buffer.WriteAsync(new byte[] { 9, 10, 11, 12 }, default).AsTask(); Assert.False(writing.IsCompleted);
        var bytes = new byte[8]; Assert.Equal(8, fast.Read(bytes)); Assert.False(writing.IsCompleted);
        Assert.Equal(4, slow.Read(bytes.AsSpan(0, 4))); await writing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(8, buffer.PeakBufferedBytes); Assert.Equal(12, buffer.ProducedBytes);
        buffer.Complete(); Assert.Equal(8, slow.Read(bytes)); Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, bytes);
        Assert.Equal(4, fast.Read(bytes)); Assert.Equal(new byte[] { 9, 10, 11, 12 }, bytes[..4]); Assert.Equal(0, fast.Read(bytes));
    }
    [Fact] public async Task DroppingFailedReaderUnblocksProducerAndDoesNotMoveHealthyCursor()
    {
        using var buffer = new StreamingPcmBuffer(8, 4); using var healthy = buffer.CreateReader(); var failed = buffer.CreateReader();
        await buffer.WriteAsync(new byte[8], default); healthy.Read(new byte[8]);
        var writing = buffer.WriteAsync(new byte[] { 1, 2 }, default).AsTask(); Assert.False(writing.IsCompleted);
        failed.Dispose(); await writing.WaitAsync(TimeSpan.FromSeconds(2));
        buffer.Complete(); var tail = new byte[2]; Assert.Equal(2, healthy.Read(tail)); Assert.Equal(new byte[] { 1, 2 }, tail);
    }
    [Fact] public async Task BlockedProducerCancelsAndLateWritesCannotAppend()
    {
        using var buffer = new StreamingPcmBuffer(4, 2); using var reader = buffer.CreateReader(); using var cancellation = new CancellationTokenSource();
        await buffer.WriteAsync(new byte[4], default); var writing = buffer.WriteAsync(new byte[2], cancellation.Token).AsTask();
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        buffer.Complete(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buffer.WriteAsync(new byte[2], default).AsTask()); Assert.Equal(4, buffer.ProducedBytes);
    }
    [Fact] public async Task ReadyWaitsForThresholdButAllowsShortNormallyCompletedAudio()
    {
        using var buffer = new StreamingPcmBuffer(12, 8); using var reader = buffer.CreateReader();
        var ready = buffer.WaitForReadyAsync(default); await buffer.WriteAsync(new byte[4], default); Assert.False(ready.IsCompleted);
        buffer.Complete(); await ready.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidOperationException>(() => buffer.CreateReader());
        using var empty = new StreamingPcmBuffer(12, 8); empty.Complete(); await Assert.ThrowsAsync<UserFeedbackException>(() => empty.WaitForReadyAsync(default));
        await Assert.ThrowsAsync<UserFeedbackException>(() => buffer.WriteAsync(new byte[1], default).AsTask());
    }
    [Fact] public void DefaultStreamingAppliesOnlyToVerifiedModelAndProvider()
    {
        Assert.True(new Settings(Provider: TtsProvider.MiMo).UsesMiMoStreaming);
        Assert.False(new Settings(Provider: TtsProvider.MiMo, MiMoStreaming: false).UsesMiMoStreaming);
        Assert.False(new Settings(Provider: TtsProvider.MiMo, MiMoModel: "future-model").UsesMiMoStreaming);
        foreach (var provider in new[] { TtsProvider.Windows, TtsProvider.Azure, TtsProvider.Edge, TtsProvider.OpenAiCompatible })
            Assert.False(new Settings(Provider: provider).UsesMiMoStreaming);
        var old = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"Provider\":3}")!; Assert.True(old.MiMoStreaming); Assert.True(old.UsesMiMoStreaming);
        var disabled = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(old with { MiMoStreaming = false }))!;
        Assert.False(disabled.MiMoStreaming); Assert.False(disabled.UsesMiMoStreaming);
    }
}
