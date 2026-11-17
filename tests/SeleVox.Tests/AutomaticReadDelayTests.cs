using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class AutomaticReadDelayTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1.5)]
    [InlineData(false, true, 1.5)]
    [InlineData(true, false, 1.5)]
    [InlineData(true, true, 1.5)]
    public async Task OnlySubmitsOnceAfterDelay(bool ocr, bool streaming, double seconds)
    {
        var clock = new DelayClock();
        var backend = new Audio();
        using var reading = new ReadingSession(backend, backend);
        using var selection = new AutomaticSelection(clock);
        var settings = new Settings(Provider: TtsProvider.MiMo, MiMoStreaming: streaming, AllowAutomaticMiMo: true)
        { OcrAction = OcrCompletionAction.Read, AutomaticReadDelaySeconds = seconds };
        using var followUp = new OcrFollowUp(settings, clock);
        Task? submitted = null;
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reading.Changed += status => { if (status.State == ReadingState.Playing) playing.TrySetResult(); };
        var pending = ocr
            ? followUp.RunAsync(new("fixture", TextSource.ScreenOcr), () => true, (_, _, _) => Task.CompletedTask,
                (text, options, token) => submitted = reading.ReadAsync(_ => Task.FromResult(text), options, true, cancellation: token),
                _ => Assert.Fail())
            : selection.RequestAsync("fixture window", _ => Task.FromResult("fixture"), () => true,
                text => submitted = reading.ReadTextAsync(text, settings), delay: TimeSpan.FromSeconds(seconds));
        if (seconds > 0)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), clock.DueTime);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, backend.RegularCalls + backend.StreamingCalls);
            Assert.Equal(ReadingState.Idle, reading.Status.State);
            clock.Expire();
        }
        else Assert.Null(clock.DueTime);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(streaming ? 0 : 1, backend.RegularCalls);
        Assert.Equal(streaming ? 1 : 0, backend.StreamingCalls);
        reading.Stop();
        await submitted!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SupersededSelectionsDoNotDeduplicateAndOnlyLatestCanSubmit()
    {
        var clock = new DelayClock();
        using var selection = new AutomaticSelection(clock);
        var submitted = new List<string>();
        Task Select(string text) => selection.RequestAsync("fixture window", _ => Task.FromResult(text), () => true,
            submitted.Add, delay: TimeSpan.FromSeconds(1));
        var first = Select("first");
        var latest = Select("second");
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(submitted);
        clock.Expire();
        await latest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["second"], submitted);
        await Select("second").WaitAsync(TimeSpan.FromSeconds(5));
        var reselected = Select("first");
        clock.Expire();
        await reselected.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["second", "first"], submitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionRejectsInFlightInvalidationAndRechecksEligibility(bool cancel)
    {
        var clock = new DelayClock();
        using var selection = new AutomaticSelection(clock);
        var allowed = true;
        var submitted = false;
        var task = selection.RequestAsync("fixture window", _ => Task.FromResult("fixture"), () => allowed,
            _ => submitted = true, delay: TimeSpan.FromSeconds(1));
        Assert.NotNull(clock.DueTime);
        if (cancel) selection.Invalidate();
        else { allowed = false; clock.Expire(); }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(submitted);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("settings")]
    [InlineData("stale")]
    public async Task OcrDelayRejectsCanceledOrStaleResult(string change)
    {
        var clock = new DelayClock();
        using var followUp = new OcrFollowUp(new Settings { OcrAction = OcrCompletionAction.Read, AutomaticReadDelaySeconds = 1 }, clock);
        var current = true;
        var task = followUp.RunAsync(new("fixture", TextSource.ScreenOcr), () => current, (_, _, _) => Task.CompletedTask,
            (_, _, _) => Assert.Fail(), _ => Assert.Fail());
        Assert.NotNull(clock.DueTime);
        if (change == "stale") { current = false; clock.Expire(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        else
        {
            if (change == "close") followUp.Dispose();
            else followUp.CancelPendingDelayedRead();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class DelayClock : TimeProvider
    {
        private Action? expire;
        public TimeSpan? DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            expire = () => callback(state);
            return new Timer();
        }
        public void Expire() => expire!();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Audio : ISpeechSynthesis, IStreamingSpeechSynthesis, IAudioPlaybackFactory, IStreamingPlaybackFactory
    {
        public int RegularCalls, StreamingCalls;
        public bool UsesStreaming(Settings settings) => settings.UsesMiMoStreaming;
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        {
            ++RegularCalls;
            return Task.FromResult<Stream>(new MemoryStream());
        }
        public async Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation)
        {
            ++StreamingCalls;
            await buffer.WriteAsync(new byte[48000], cancellation);
        }
        public IAudioPlayback CreatePlayback(Stream audio, Settings settings) => new Player(audio);
        public IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings) => new Player(buffer.CreateReader());
        private sealed class Player(IDisposable input) : IAudioPlayback
        {
            public event Action<Exception?>? Finished;
            public void Play() { }
            public void Pause() { }
            public void Stop() => Finished?.Invoke(null);
            public void Dispose() => input.Dispose();
        }
    }
}
