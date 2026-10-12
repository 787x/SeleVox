using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class MultiOutputTests
{
    [Fact] public void ReadersHaveIndependentPositionsAndSelectedIdsOnly()
    {
        var audio = Fixture(); var opened = new List<(string Id, Player Player)>();
        using var group = AudioOutputs.CreatePlayback(audio, new(OutputDeviceIds: ["headphones", "cable", "headphones"]),
            (id, stream, volume) => { var player = new Player(stream); opened.Add((id, player)); Assert.Equal(0.8f, volume); return player; });
        Assert.False(audio.CanRead); Assert.Equal(new[] { "headphones", "cable" }, opened.Select(o => o.Id));
        var first = opened[0].Player.Stream!; var second = opened[1].Player.Stream!;
        first.ReadByte(); first.ReadByte(); Assert.Equal(0, second.Position);
        Assert.Equal(2, first.Position); Assert.False(first.CanWrite);
        Assert.Equal(first.ReadByte(), ReadThirdByte(second));
        group.Stop(); group.Dispose(); Assert.All(opened, o => Assert.False(o.Player.Stream!.CanRead));
    }
    private static int ReadThirdByte(Stream stream) { stream.ReadByte(); stream.ReadByte(); return stream.ReadByte(); }
    internal static MemoryStream Fixture()
    {
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(24000, 16, 1)))
            writer.Write(new byte[4800], 0, 4800);
        return new MemoryStream(memory.ToArray());
    }
    [Fact] public void MissingDevicesFallBackOnlyWhenNoneCanBeOpened()
    {
        var opened = new List<string>();
        IAudioPlayback? Open(string id, Stream stream, float _) { opened.Add(id); if (id == "missing") throw new IOException("private"); return new Player(stream); }
        using var partial = AudioOutputs.CreatePlayback(Fixture(), new(OutputDeviceIds: ["missing", "cable"]), Open);
        Assert.Equal(new[] { "missing", "cable" }, opened); Assert.Contains("跳过", partial.Warning);
        opened.Clear();
        using var fallback = AudioOutputs.CreatePlayback(Fixture(), new(OutputDeviceIds: ["missing"]), Open);
        Assert.Equal(new[] { "missing", Settings.DefaultOutput }, opened); Assert.Contains("回退", fallback.Warning);
        opened.Clear();
        using var defaults = AudioOutputs.CreatePlayback(Fixture(), new(), Open);
        Assert.Equal(new[] { Settings.DefaultOutput }, opened); Assert.Null(defaults.Warning);
    }
    [Fact] public void NoOutputsGivesSafeFeedbackAndDisposesFailedStreams()
    {
        var streams = new List<Stream>();
        var error = Assert.Throws<UserFeedbackException>(() => AudioOutputs.CreatePlayback(Fixture(), new(OutputDeviceIds: ["missing"]),
            (_, stream, _) => { streams.Add(stream); throw new IOException("private"); }));
        Assert.DoesNotContain("private", error.Message); Assert.All(streams, s => Assert.False(s.CanRead));
    }
    [Fact] public async Task CompletionWaitsForAllAndPartialFailureDoesNotStopSurvivor()
    {
        var first = new Player(); var second = new Player(); using var group = new MultiOutputPlayback([first, second]);
        var completion = new TaskCompletionSource<Exception?>(); group.Finished += error => completion.TrySetResult(error);
        var warning = new TaskCompletionSource(); group.WarningChanged += () => warning.TrySetResult();
        group.Play(); first.Finish(new IOException()); await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(completion.Task.IsCompleted); Assert.False(second.Disposed);
        group.Pause(); group.Play(); Assert.Equal(1, second.Pauses); Assert.Equal(2, second.Plays);
        second.Finish(null); Assert.Null(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(second.Disposed);
    }
    [Fact] public async Task NaturalCompletionWaitsForEveryOutputAndCannotReplayCompletedChild()
    {
        var first = new Player(); var second = new Player(); using var group = new MultiOutputPlayback([first, second]);
        var completion = new TaskCompletionSource<Exception?>(); group.Finished += error => completion.TrySetResult(error);
        group.Play(); first.Finish(null); await WaitUntil(() => first.Disposed);
        Assert.False(completion.Task.IsCompleted); group.Pause(); group.Play(); Assert.Equal(1, first.Plays);
        second.Finish(null); Assert.Null(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact] public async Task AllFailuresAreReportedAndSynchronousPlayFailureIsIsolated()
    {
        var first = new Player { FailPlay = true }; var second = new Player(); using var group = new MultiOutputPlayback([first, second]);
        var completion = new TaskCompletionSource<Exception?>(); group.Finished += error => completion.TrySetResult(error);
        group.Play(); Assert.True(first.Disposed); Assert.Equal(1, second.Plays); Assert.False(completion.Task.IsCompleted);
        second.Finish(new IOException()); Assert.NotNull(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact] public async Task StopAndDisposeIgnoreAlreadyCapturedLateCallbacks()
    {
        var first = new Player(); var second = new Player(); var group = new MultiOutputPlayback([first, second]);
        var late = first.FinishedCallback; var completions = 0; group.Finished += _ => ++completions;
        group.Play(); group.Pause(); group.Play(); Assert.All(new[] { first, second }, p => { Assert.Equal(2, p.Plays); Assert.Equal(1, p.Pauses); });
        group.Stop(); group.Dispose(); late!(new IOException()); await Task.Delay(100);
        Assert.Equal(0, completions); Assert.All(new[] { first, second }, p => { Assert.True(p.Disposed); Assert.Equal(1, p.Stops); });
    }
    [Fact] public void CleanupFailureStillStopsAndDisposesEveryOutput()
    {
        var first = new Player { FailStop = true }; var second = new Player(); var group = new MultiOutputPlayback([first, second]);
        Assert.Throws<UserFeedbackException>(group.Stop); group.Dispose();
        Assert.Equal(1, second.Stops); Assert.True(first.Disposed); Assert.True(second.Disposed);
    }
    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Player(Stream? stream = null) : IAudioPlayback
    {
        public Stream? Stream = stream;
        public bool Disposed, FailPlay, FailStop; public int Plays, Pauses, Stops;
        public event Action<Exception?>? Finished;
        public Action<Exception?>? FinishedCallback => Finished;
        public void Finish(Exception? error) => Finished?.Invoke(error);
        public void Play() { if (FailPlay) throw new IOException(); ++Plays; }
        public void Pause() => ++Pauses;
        public void Stop() { ++Stops; if (FailStop) throw new IOException(); }
        public void Dispose() { Stream?.Dispose(); Disposed = true; }
    }
}
