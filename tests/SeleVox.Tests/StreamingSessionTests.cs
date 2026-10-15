using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class StreamingSessionTests
{
    [Fact] public async Task SingingBufferingExplainsFirstEventWaitAndRemainsCancellable()
    {
        var server = new MiMoStreamingTests.DelayedStream("", "");
        using var backend = new Backend(_ => server); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), Settings() with { MiMoOverallStyles = ["唱歌"] });
        await Wait(() => backend.Players.Count == 2 && session.Status.Message.Contains("首次生成可能较慢"));
        session.Stop(); await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(ReadingState.Idle, session.Status.State);
    }
    private static Settings Settings(bool automatic = false) => new(Provider: TtsProvider.MiMo, AllowAutomaticMiMo: automatic, OutputDeviceIds: ["first", "second"]);
    private static readonly byte[] Pcm = Enumerable.Range(0, 9600).Select(i => (byte)i).ToArray();
    [Fact] public async Task StartsBeforeNetworkDoneAndCompletesOnlyAfterBothIndependentOutputsDrain()
    {
        var server = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), "data: [DONE]\n\n");
        using var backend = new Backend(_ => server); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), Settings());
        await Wait(() => backend.Players.Count == 2 && backend.Players.All(p => p.Plays == 1));
        Assert.False(server.Release.Task.IsCompleted); Assert.False(reading.IsCompleted); Assert.Equal(ReadingState.Playing, session.Status.State);
        var deniedStatus = session.Status;
        await session.ReadTextAsync("denied", Settings()); Assert.Same(deniedStatus, session.Status); Assert.All(backend.Players, p => Assert.Equal(0, p.Stops));
        session.TogglePause(); Assert.Equal(ReadingState.Paused, session.Status.State); session.TogglePause(); Assert.Equal(ReadingState.Playing, session.Status.State); Assert.Equal(1, backend.Requests);
        server.Release.TrySetResult(); await Wait(() => backend.Buffers[0].IsComplete);
        Assert.Equal(Pcm, backend.Players[0].Drain()); await Wait(() => backend.Players[0].Disposed);
        Assert.Equal(ReadingState.Playing, session.Status.State); Assert.False(reading.IsCompleted);
        Assert.Equal(Pcm, backend.Players[1].Drain()); await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(ReadingState.Idle, session.Status.State);
    }
    [Theory] [InlineData("stop")] [InlineData("dispose")] [InlineData("replace")]
    public async Task StopExitAndReplacementRejectLateDataAndCallbacks(string action)
    {
        var sources = new List<MiMoStreamingTests.DelayedStream>();
        using var backend = new Backend(_ => { var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), MiMoStreamingTests.Event([12, 34]) + "data: [DONE]\n\n"); sources.Add(source); return source; });
        using var session = new ReadingSession(backend, backend); var first = session.ReadAsync(_ => Task.FromResult("fixture"), Settings());
        await Wait(() => backend.Players.Count == 2 && backend.Players[0].Plays > 0);
        Task? second = null;
        if (action == "stop") session.Stop(); else if (action == "dispose") session.Dispose(); else second = session.ReadAsync(_ => Task.FromResult("new fixture"), Settings());
        await first.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(sources[0].Disposed); Assert.All(backend.Players.Take(2), p => Assert.True(p.Disposed));
        sources[0].Release.TrySetResult(); Assert.Equal(Pcm.Length, backend.Buffers[0].ProducedBytes);
        if (second is not null)
        {
            await Wait(() => backend.Players.Count == 4 && backend.Players[2].Plays > 0);
            backend.Players[0].Finish(new IOException("late-private")); await Task.Delay(30); Assert.Equal(ReadingState.Playing, session.Status.State);
            session.Stop(); await second.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(sources[1].Disposed);
        }
    }
    [Fact] public async Task RevocationDuringPlaybackStopsProductionButDrainsOnlyAcceptedPcm()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), MiMoStreamingTests.Event([12, 34]) + "data: [DONE]\n\n");
        using var backend = new Backend(_ => source); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadTextAsync("fixture", Settings(true)); await Wait(() => backend.Players.Count == 2 && backend.Players[0].Plays > 0);
        session.CancelPendingAutomatic(); await Wait(() => source.Disposed); Assert.All(backend.Players, p => { Assert.False(p.Disposed); Assert.Equal(0, p.Stops); });
        source.Release.TrySetResult(); Assert.Equal(Pcm.Length, backend.Buffers[0].ProducedBytes);
        Assert.Equal(Pcm, backend.Players[0].Drain()); Assert.Equal(Pcm, backend.Players[1].Drain());
        await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(ReadingState.Idle, session.Status.State); Assert.Contains("已取消", session.Status.Message); Assert.DoesNotContain("朗读完成", session.Status.Message);
    }
    [Fact] public async Task RevocationBeforePlaybackReleasesPendingPlayersAndNeverPlays()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event([1, 2]), "data: [DONE]\n\n");
        using var backend = new Backend(_ => source); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadTextAsync("fixture", Settings(true)); await Wait(() => backend.Buffers.Count == 1 && backend.Buffers[0].ProducedBytes == 2);
        Assert.False(reading.IsCompleted, session.Status.Message);
        session.CancelPendingAutomatic(); await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.All(backend.Players, p => { Assert.True(p.Disposed); Assert.Equal(0, p.Plays); });
        Assert.Equal(ReadingState.Idle, session.Status.State); Assert.True(source.Disposed);
    }
    [Fact] public async Task ProducerFailureAfterStartingIsNeverReportedAsNaturalCompletion()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), "");
        using var backend = new Backend(_ => source); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), Settings()); await Wait(() => backend.Players.Count == 2 && backend.Players[0].Plays > 0);
        source.Release.TrySetResult(); await Wait(() => backend.Buffers[0].IsComplete);
        Assert.False(reading.IsCompleted); Assert.Contains("流中断", session.Status.Message);
        Assert.Equal(Pcm, backend.Players[0].Drain()); Assert.Equal(Pcm, backend.Players[1].Drain());
        await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(ReadingState.Error, session.Status.State); Assert.Contains("流中断", session.Status.Message);
        Assert.All(backend.Players, p => Assert.True(p.Disposed));
    }
    [Fact] public async Task OneDeviceFailureReleasesItsCursorWhileOtherOutputFinishes()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), "data: [DONE]\n\n");
        using var backend = new Backend(_ => source); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), Settings()); await Wait(() => backend.Players.Count == 2 && backend.Players[0].Plays > 0);
        backend.Players[0].Finish(new IOException("private")); await Wait(() => backend.Players[0].Disposed); Assert.False(backend.Players[1].Disposed);
        source.Release.TrySetResult(); await Wait(() => backend.Buffers[0].IsComplete); Assert.Equal(Pcm, backend.Players[1].Drain()); await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ReadingState.Idle, session.Status.State); Assert.Contains("部分输出", session.Status.Message);
    }
    [Fact] public async Task AllOutputFailuresCancelNetworkAndSetError()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), "data: [DONE]\n\n");
        using var backend = new Backend(_ => source); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), Settings()); await Wait(() => backend.Players.Count == 2 && backend.Players[0].Plays > 0);
        Assert.False(reading.IsCompleted, session.Status.Message);
        foreach (var player in backend.Players) player.Finish(new IOException("private"));
        await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(ReadingState.Error, session.Status.State); Assert.True(source.Disposed);
    }
    [Fact] public async Task PrematureSuccessfulDeviceCallbacksDoNotReportSuccessfulStream()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Pcm), "data: [DONE]\n\n");
        using var backend = new Backend(_ => source); using var session = new ReadingSession(backend, backend);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), Settings()); await Wait(() => backend.Players.Count == 2 && backend.Players[0].Plays > 0);
        foreach (var player in backend.Players) player.Finish(null);
        await reading.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(ReadingState.Error, session.Status.State); Assert.True(source.Disposed);
    }
    private static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class Backend(Func<int, Stream> response) : ISpeechSynthesis, IAudioPlaybackFactory, IStreamingSpeechSynthesis, IStreamingPlaybackFactory, IDisposable
    {
        public List<Player> Players = []; public List<StreamingPcmBuffer> Buffers = []; public int Requests;
        private readonly HttpClient http = new(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(response(0)))));
        public bool UsesStreaming(Settings settings) => settings.UsesMiMoStreaming;
        public async Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation)
        { ++Requests; await new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync(text, settings, buffer, cancellation); }
        public IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings)
        { Buffers.Add(buffer); return AudioOutputs.CreateStreamingPlayback(buffer, settings, (_, reader, _) => { var player = new Player(reader); Players.Add(player); return player; }); }
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation) => throw new Exception("Must use streaming path.");
        public IAudioPlayback CreatePlayback(Stream audio, Settings settings) => throw new Exception("Must use streaming playback.");
        public void Dispose() => http.Dispose();
    }
    private sealed class Player(StreamingPcmBuffer.Reader reader) : IAudioPlayback
    {
        public event Action<Exception?>? Finished;
        public int Plays, Stops; public bool Disposed;
        public void Play() => ++Plays; public void Pause() { } public void Stop() => ++Stops;
        public void Dispose() { Disposed = true; reader.Dispose(); }
        public void Finish(Exception? error) => Finished?.Invoke(error);
        public byte[] Drain()
        {
            using var bytes = new MemoryStream(); var block = new byte[16384]; int count;
            while ((count = reader.Read(block)) > 0) bytes.Write(block, 0, count);
            Finish(null); return bytes.ToArray();
        }
    }
}
