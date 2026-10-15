using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class QuestionSessionTests
{
    private sealed class Pending
    {
        public readonly TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<string> Delta = null!;
        public CancellationToken Token;
    }
    [Fact] public async Task OldLateTextCompletionAndErrorsCannotReplaceNewQuestion()
    {
        foreach (var fails in new[] { false, true })
        {
            var requests = new List<Pending>(); var reads = new List<string>();
            using var session = Create(requests, reads);
            var old = session.AskAsync("old", ChatCompletionTests.Options, "key"); requests[0].Delta("old partial");
            var next = session.AskAsync("new", ChatCompletionTests.Options, "key");
            Assert.True(requests[0].Token.IsCancellationRequested); requests[0].Delta("late");
            if (fails) requests[0].Result.SetException(new UserFeedbackException("old error")); else requests[0].Result.SetResult("old answer");
            await old; Assert.Equal("new", session.Snapshot.Question); Assert.Equal("", session.Snapshot.Answer);
            requests[1].Delta("new answer"); Assert.Empty(reads);
            requests[1].Result.SetResult("new answer"); await next;
            Assert.Equal(new[] { "new answer" }, reads); Assert.Equal(QuestionState.Completed, session.Snapshot.State);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopOrDisposeBlocksUncooperativeCompletionAndKeepsPartial(bool dispose)
    {
        var requests = new List<Pending>(); var reads = new List<string>();
        using var session = Create(requests, reads);
        var running = session.AskAsync("q", ChatCompletionTests.Options, "key"); requests[0].Delta("partial");
        if (dispose) session.Dispose(); else session.Cancel();
        requests[0].Delta("late"); requests[0].Result.SetResult("complete"); await running;
        Assert.Equal("partial", session.Snapshot.Answer); Assert.Equal(QuestionState.Cancelled, session.Snapshot.State); Assert.Empty(reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AutoReadIsOnceOnlyAfterCompleteAndManualReadDoesNotGenerate(bool auto)
    {
        var requests = new List<Pending>(); var reads = new List<string>(); var stops = 0;
        using var session = Create(requests, reads, () => ++stops);
        var running = session.AskAsync("q", ChatCompletionTests.Options with { LlmAutoRead = auto }, "key");
        requests[0].Delta("a"); requests[0].Delta("b"); Assert.Empty(reads);
        requests[0].Result.SetResult("ab"); await running; Assert.Equal(auto ? 1 : 0, reads.Count);
        await session.ReadAnswerAsync(new Settings(Provider: TtsProvider.MiMo)); Assert.Equal(auto ? 2 : 1, reads.Count); Assert.Single(requests);
        session.Cancel(); Assert.Equal("ab", session.Snapshot.Answer); Assert.Equal(QuestionState.Completed, session.Snapshot.State); Assert.True(stops >= 2);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailureOrEmptyAnswerNeverSpeaks(bool failure)
    {
        var requests = new List<Pending>(); var reads = new List<string>(); using var session = Create(requests, reads);
        var running = session.AskAsync("q", ChatCompletionTests.Options, "key"); requests[0].Delta("partial");
        if (failure) requests[0].Result.SetException(new UserFeedbackException("interrupted")); else requests[0].Result.SetResult(" ");
        await running; Assert.Equal(QuestionState.Error, session.Snapshot.State); Assert.Equal("partial", session.Snapshot.Answer); Assert.Empty(reads);
        await session.ReadAnswerAsync(new()); Assert.Empty(reads);
    }
    [Fact] public async Task InvalidActionDoesNotCancelCurrentQuestionOrStopAudio()
    {
        var requests = new List<Pending>(); var reads = new List<string>(); var stops = 0;
        using var session = Create(requests, reads, () => ++stops);
        var running = session.AskAsync("q", ChatCompletionTests.Options, "key"); var before = stops;
        await Assert.ThrowsAsync<UserFeedbackException>(() => session.AskAsync(" ", ChatCompletionTests.Options, "key"));
        await Assert.ThrowsAsync<UserFeedbackException>(() => session.AskAsync("q", ChatCompletionTests.Options with { LlmModel = "" }, "key"));
        await Assert.ThrowsAsync<UserFeedbackException>(() => session.AskAsync("q", ChatCompletionTests.Options, ""));
        Assert.False(requests[0].Token.IsCancellationRequested); Assert.Equal(before, stops);
        requests[0].Result.SetResult("answer"); await running; Assert.Single(reads);
    }
    [Fact] public async Task StopDuringSpeechStartStopsThatSpeechAndPreservesAnswer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var speechActive = false;
        using var session = new QuestionSession((_, _, _, _, _) => Task.FromResult("answer"), (_, _) =>
        { speechActive = true; entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(3))); return Task.CompletedTask; }, () => speechActive = false);
        var running = Task.Run(() => session.AskAsync("q", ChatCompletionTests.Options, "key"));
        await entered.Task; var stopped = Task.Run(session.Cancel); release.Set(); await Task.WhenAll(running, stopped);
        Assert.False(speechActive); Assert.Equal("answer", session.Snapshot.Answer);
    }
    private static QuestionSession Create(List<Pending> requests, List<string> reads, Action? stop = null)
        => new((_, _, _, delta, token) => { var pending = new Pending { Delta = delta, Token = token }; requests.Add(pending); return pending.Result.Task; },
            (text, _) => { reads.Add(text); return Task.CompletedTask; }, stop ?? (() => { }));
}
