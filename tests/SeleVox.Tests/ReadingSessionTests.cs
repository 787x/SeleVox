using SeleVox.Core;
namespace SeleVox.Tests;
public sealed class ReadingSessionTests
{
    private static Task<string> Capture(CancellationToken _) => Task.FromResult("non-sensitive fixture");
    [Fact] public async Task InvalidAutomaticCaptureLeavesExistingPlaybackUntouched()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend); using var automatic = new AutomaticSelection();
        var read = session.ReadAsync(Capture, new()); backend.Pending[0].SetResult(new TrackedStream()); await read;
        var player = backend.Players[0];
        await automatic.RequestAsync("app", _ => Task.FromResult(""), () => true, text => _ = session.ReadTextAsync(text, new()));
        await automatic.RequestAsync("app", _ => throw new IOException(), () => true, text => _ = session.ReadTextAsync(text, new()));
        session.CancelPendingAutomatic();
        Assert.Equal(ReadingState.Playing, session.Status.State); Assert.False(player.Disposed); Assert.Single(backend.Pending);
    }
    [Fact] public async Task DisableRejectsAutomaticSynthesisButPreservesStartedPlayback()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadTextAsync("fixture", new()); session.CancelPendingAutomatic();
        var stream = new TrackedStream(); backend.Pending[0].SetResult(stream); await read;
        Assert.True(stream.Disposed); Assert.Empty(backend.Players);
        read = session.ReadTextAsync("other fixture", new()); backend.Pending[1].SetResult(new TrackedStream()); await read;
        session.CancelPendingAutomatic(); Assert.Equal(ReadingState.Playing, session.Status.State);
        Assert.False(backend.Players[0].Disposed);
    }
    [Fact] public async Task AutomaticErrorsStayQuietAndManualErrorsNotify()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadTextAsync("fixture", new()); backend.Pending[0].SetException(new IOException()); await read;
        Assert.False(session.Status.Notify);
        await session.ReadAsync(_ => throw new IOException(), new()); Assert.True(session.Status.Notify);
    }
    [Fact] public async Task StopRejectsLateNonCooperativeSynthesisAndDisposesAudio()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadAsync(Capture, new());
        session.Stop(); var stream = new TrackedStream(); backend.Pending[0].SetResult(stream);
        await read;
        Assert.True(stream.Disposed); Assert.Empty(backend.Players); Assert.Equal(ReadingState.Idle, session.Status.State);
    }
    [Fact] public async Task NewRequestRejectsOldAudioAndSerializesSynthesis()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var first = session.ReadAsync(Capture, new());
        var second = session.ReadAsync(Capture, new());
        Assert.Single(backend.Pending);
        var old = new TrackedStream(); backend.Pending[0].SetResult(old); await first;
        await WaitUntil(() => backend.Pending.Count == 2);
        backend.Pending[1].SetResult(new TrackedStream()); await second;
        Assert.True(old.Disposed); Assert.Single(backend.Players); Assert.Equal(ReadingState.Playing, session.Status.State);
    }
    [Fact] public async Task PauseResumesSamePlayerWithoutSynthesizingAgain()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadAsync(Capture, new()); backend.Pending[0].SetResult(new TrackedStream()); await read;
        var player = backend.Players[0];
        session.TogglePause(); Assert.Equal(ReadingState.Paused, session.Status.State); Assert.Equal(1, player.Pauses);
        session.TogglePause(); Assert.Equal(ReadingState.Playing, session.Status.State); Assert.Equal(2, player.Plays);
        Assert.Single(backend.Pending);
        session.Stop(); Assert.True(player.Disposed); Assert.Equal(ReadingState.Idle, session.Status.State);
    }
    [Fact] public async Task ReplacementDisposesPlayingSessionAndIgnoresItsLateCallback()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var first = session.ReadAsync(Capture, new()); backend.Pending[0].SetResult(new TrackedStream()); await first;
        var old = backend.Players[0]; var second = session.ReadAsync(Capture, new());
        Assert.True(old.Disposed);
        old.Finish(new IOException());
        backend.Pending[1].SetResult(new TrackedStream()); await second;
        Assert.Equal(ReadingState.Playing, session.Status.State); Assert.False(backend.Players[1].Disposed);
    }
    [Fact] public async Task CompletionAndDeviceErrorReleasePlayback()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadAsync(Capture, new()); backend.Pending[0].SetResult(new TrackedStream()); await read;
        backend.Players[0].Finish(null); await WaitUntil(() => session.Status.State == ReadingState.Idle);
        Assert.True(backend.Players[0].Disposed);
        read = session.ReadAsync(Capture, new()); backend.Pending[1].SetResult(new TrackedStream()); await read;
        backend.Players[1].Finish(new IOException()); await WaitUntil(() => session.Status.State == ReadingState.Error);
        Assert.True(backend.Players[1].Disposed);
    }
    [Fact] public async Task CaptureFailureNeverSynthesizesAndDoesNotExposeExceptionText()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        await session.ReadAsync(_ => throw new IOException("sensitive-content"), new());
        Assert.Empty(backend.Pending); Assert.Equal(ReadingState.Error, session.Status.State);
        Assert.DoesNotContain("sensitive-content", session.Status.Message);
    }
    [Fact] public async Task DisposeRejectsLateSynthesis()
    {
        var backend = new Audio(); var session = new ReadingSession(backend);
        var read = session.ReadAsync(Capture, new()); session.Dispose();
        var stream = new TrackedStream(); backend.Pending[0].SetResult(stream); await read;
        Assert.True(stream.Disposed); Assert.Empty(backend.Players);
    }
    [Fact] public async Task SynthesisFailureCanBeFollowedBySuccessfulNewRequest()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadAsync(Capture, new());
        backend.Pending[0].SetException(new IOException("sensitive-content")); await read;
        Assert.Equal(ReadingState.Error, session.Status.State); Assert.DoesNotContain("sensitive-content", session.Status.Message);
        read = session.ReadAsync(Capture, new()); backend.Pending[1].SetResult(new TrackedStream()); await read;
        Assert.Equal(ReadingState.Playing, session.Status.State);
    }
    [Fact] public async Task StopFailureStillDisposesPlayerAndDoesNotEscapeToUi()
    {
        var backend = new Audio(); using var session = new ReadingSession(backend);
        var read = session.ReadAsync(Capture, new()); backend.Pending[0].SetResult(new TrackedStream()); await read;
        backend.Players[0].FailStop = true; session.Stop();
        Assert.True(backend.Players[0].Disposed); Assert.Equal(ReadingState.Error, session.Status.State);
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class TrackedStream : MemoryStream
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Audio : ISpeechAudio
    {
        public List<TaskCompletionSource<Stream>> Pending = [];
        public List<Player> Players = [];
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(completion); return completion.Task;
        }
        public IAudioPlayback CreatePlayback(Stream audio, float volume) { var player = new Player(audio); Players.Add(player); return player; }
    }
    private sealed class Player(Stream audio) : IAudioPlayback
    {
        public event Action<Exception?>? Finished;
        public int Plays, Pauses; public bool Disposed, FailStop;
        public void Play() => Plays++;
        public void Pause() => Pauses++;
        public void Stop() { if (FailStop) throw new IOException("device failure"); }
        public void Dispose() { Disposed = true; audio.Dispose(); }
        public void Finish(Exception? error) => Finished?.Invoke(error);
    }
}
