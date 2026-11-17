using System.Collections.Concurrent;
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
    [InlineData(true, false, 0)]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IneligibleSelectionsLeaveDelayedOcrIntact(bool streaming)
    {
        using var fixture = new InterleavedReads(streaming);
        var prior = new List<string>();
        await fixture.Selection.RequestAsync("fixture window", _ => Task.FromResult("duplicate"), () => true, prior.Add);
        Assert.Equal(["duplicate"], prior);
        var ocr = fixture.StartOcr();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Selection.Invalidate(); // Ordinary clicks only invalidate mouse work.
        await fixture.Select(_ => Task.FromResult(""));
        await fixture.Select(_ => Task.FromResult("duplicate"));
        string? feedback = null;
        await fixture.Select(_ => throw new UserFeedbackException("UIA failed"), feedback: message => feedback = message);
        Assert.Equal("UIA failed", feedback);
        await fixture.Select(_ => Task.FromResult("disallowed rule"), options => options.AllowsAutomatic(@"C:\Other\app.exe"));
        await fixture.Select(_ => Task.FromResult("disallowed provider"), options =>
            (options with { AllowAutomaticMiMo = false }).AllowsAutomaticProvider);
        var staleCapture = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = fixture.Select(_ => staleCapture.Task);
        fixture.Selection.Invalidate();
        staleCapture.SetResult("stale");
        await stale.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ocr.IsCompleted);
        Assert.Empty(fixture.Backend.Requests);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await ocr.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AssertOnlyPlayed("OCR A");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfirmedAutoSpeakSupersedesOcrBeforeItsEarlierDeadline(bool streaming, bool cancelSelection)
    {
        using var fixture = new InterleavedReads(streaming);
        var ocr = fixture.StartOcr();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var speak = fixture.Select(_ => Task.FromResult("Auto Speak B"));
        Assert.False(speak.IsCompleted);

        if (cancelSelection) fixture.Selection.Invalidate();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2)); // A's original deadline, one second before B.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ocr.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(fixture.Backend.Requests);
        Assert.Equal(ReadingState.Idle, fixture.Reading.Status.State);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await speak.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelSelection)
        {
            Assert.Empty(fixture.Backend.Requests);
            Assert.Equal(ReadingState.Idle, fixture.Reading.Status.State);
        }
        else await fixture.AssertOnlyPlayed("Auto Speak B");
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

    private sealed class InterleavedReads : IDisposable
    {
        private readonly Settings settings;
        private readonly OcrFollowUp followUp;
        private readonly TaskCompletionSource playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? submitted;
        public DelayClock Clock { get; } = new();
        public AutomaticSelection Selection { get; }
        public Audio Backend { get; } = new();
        public ReadingSession Reading { get; }

        public InterleavedReads(bool streaming)
        {
            settings = new(Provider: TtsProvider.MiMo, MiMoStreaming: streaming, AllowAutomaticMiMo: true, AutoSpeak: true,
                Applications: [new(@"C:\Fixture\app.exe")])
            { OcrAction = OcrCompletionAction.Read, AutomaticReadDelaySeconds = 3 };
            Selection = new(Clock);
            followUp = new(settings, Clock);
            Reading = new(Backend, Backend);
            Reading.Changed += status => { if (status.State == ReadingState.Playing) playing.TrySetResult(); };
        }

        public Task StartOcr() => followUp.RunAsync(new("OCR A", TextSource.ScreenOcr), () => true, (_, _, _) => Task.CompletedTask,
            (text, options, token) => submitted = Reading.ReadAsync(_ => Task.FromResult(text), options, true, cancellation: token),
            _ => Assert.Fail());

        public Task Select(Func<CancellationToken, Task<string>> capture, Func<Settings, bool>? allowed = null, Action<string>? feedback = null) =>
            Selection.RequestAsync("fixture window", capture,
                () => allowed?.Invoke(settings) ?? (settings.AllowsAutomatic(@"C:\Fixture\app.exe") && settings.AllowsAutomaticProvider),
                text => submitted = Reading.ReadTextAsync(text, settings),
                feedback, TimeSpan.FromSeconds(settings.AutomaticReadDelaySeconds), followUp.CancelPendingDelayedRead);

        public async Task AssertOnlyPlayed(string text)
        {
            await playing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([text], Backend.Requests.ToArray());
            Assert.Equal(settings.UsesMiMoStreaming ? 0 : 1, Backend.RegularCalls);
            Assert.Equal(settings.UsesMiMoStreaming ? 1 : 0, Backend.StreamingCalls);
            Reading.Stop();
            await submitted!.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Selection.Dispose();
            followUp.Dispose();
            Reading.Dispose();
        }
    }

    private sealed class DelayClock : TimeProvider
    {
        private readonly List<Timer> timers = [];
        private TimeSpan elapsed;
        public TimeSpan? DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            var timer = new Timer(elapsed + dueTime, () => callback(state));
            timers.Add(timer);
            return timer;
        }
        public void Expire() => Advance(DueTime!.Value);
        public void Advance(TimeSpan amount)
        {
            elapsed += amount;
            foreach (var timer in timers.Where(timer => timer.Deadline <= elapsed).OrderBy(timer => timer.Deadline).ToArray())
            {
                timers.Remove(timer);
                timer.Fire();
            }
        }
        private sealed class Timer(TimeSpan deadline, Action callback) : ITimer
        {
            private Action? pending = callback;
            public TimeSpan Deadline => deadline;
            public void Fire() => Interlocked.Exchange(ref pending, null)?.Invoke();
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref pending, null);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Audio : ISpeechSynthesis, IStreamingSpeechSynthesis, IAudioPlaybackFactory, IStreamingPlaybackFactory
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public int RegularCalls, StreamingCalls;
        public bool UsesStreaming(Settings settings) => settings.UsesMiMoStreaming;
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        {
            ++RegularCalls;
            Requests.Enqueue(text);
            return Task.FromResult<Stream>(new MemoryStream());
        }
        public async Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation)
        {
            ++StreamingCalls;
            Requests.Enqueue(text);
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
