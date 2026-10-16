using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class Pr32Tests
{
    internal static Settings OcrOptions => ChatCompletionTests.Options with { AutoAsk = false, AllowOcrLlm = true,
        OcrLlmAuthorizedBaseUrl = Settings.NormalizeServiceUrl(ChatCompletionTests.Options.LlmBaseUrl), OcrAction = OcrCompletionAction.AskAi };
    [Fact] public void OldSettingsDefaultAndAddressBoundIndependentConsent()
    {
        var old = JsonSerializer.Deserialize<Settings>("{}")!;
        Assert.Equal(OcrCompletionAction.ShowResult, old.OcrAction); Assert.False(old.AllowsOcrLlm);
        var s = OcrOptions;
        Assert.True(s.AllowsOcrLlm); Assert.False(s.AutoAsk);
        Assert.True((s with { LlmBaseUrl = s.LlmBaseUrl + "/" }).AllowsOcrLlm);
        Assert.False((s with { LlmBaseUrl = "https://different.invalid/v1" }).AllowsOcrLlm);
        Assert.False((s with { AllowOcrLlm = false, AllowAutomaticLlm = true, LlmAuthorizedBaseUrl = s.LlmBaseUrl }).AllowsOcrLlm);
        Assert.False((s with { OcrAction = (OcrCompletionAction)99 }).IsValid);
        var roundTrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(s, roundTrip);
    }
    [Theory] [InlineData(OcrCompletionAction.ShowResult, 0, 0)] [InlineData(OcrCompletionAction.Read, 1, 0)] [InlineData(OcrCompletionAction.AskAi, 0, 1)]
    public async Task DeferredActionUsesOriginalSnapshotExactlyOnce(OcrCompletionAction action, int expectedReads, int expectedAsks)
    {
        var options = OcrOptions with { OcrAction = action }; using var run = new OcrFollowUp(options);
        var reads = 0; var asks = 0;
        Task Check(string text, Settings s, CancellationToken token) { Assert.Same(options, s); Assert.Equal("original", text); return Task.CompletedTask; }
        void Read(string text, Settings s, CancellationToken token) { Assert.Equal("original", text); Assert.Same(options, s); ++reads; }
        void Ask(string text) { Assert.Equal("original", text); ++asks; }
        await run.RunAsync(new("original", TextSource.ScreenOcr), () => true, Check, Read, Ask);
        await run.RunAsync(new("edited", TextSource.ScreenOcr), () => true, Check, Read, Ask);
        Assert.Equal(expectedReads, reads); Assert.Equal(expectedAsks, asks);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopReplacementOrSettingsChangeDuringPrecheckBlocksRead(bool cancel)
    {
        using var run = new OcrFollowUp(OcrOptions with { OcrAction = OcrCompletionAction.Read });
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); var current = true; var actions = 0;
        var task = run.RunAsync(new("fixture", TextSource.ScreenOcr), () => current,
            async (_, _, _) => { entered.SetResult(); await release.Task; }, (_, _, _) => ++actions, _ => ++actions);
        await entered.Task;
        if (cancel) run.Dispose(); else current = false;
        release.SetResult(); await task; Assert.Equal(0, actions);
    }
    [Theory] [InlineData("")] [InlineData(" \n ")]
    public async Task EmptyNeverActs(string text)
    {
        using var run = new OcrFollowUp(OcrOptions); var actions = 0;
        await run.RunAsync(new(text, TextSource.ScreenOcr), () => true, (_, _, _) => Task.CompletedTask, (_, _, _) => ++actions, _ => ++actions);
        Assert.Equal(0, actions);
    }
    [Fact] public async Task MissingConsentOrFailedPrecheckNeverActsOrRetries()
    {
        foreach (var action in new[] { OcrCompletionAction.Read, OcrCompletionAction.AskAi })
        {
            using var run = new OcrFollowUp(OcrOptions with { OcrAction = action, AllowOcrLlm = false }); var actions = 0;
            Task Start() => run.RunAsync(new("fixture", TextSource.ScreenOcr), () => true,
                (_, _, _) => throw new UserFeedbackException("not configured"), (_, _, _) => ++actions, _ => ++actions);
            await Assert.ThrowsAsync<UserFeedbackException>(Start); await Start(); Assert.Equal(0, actions);
        }
    }
    [Fact] public async Task CaptureMustSettleBeforeDeferredActionAndLateCaptureNeverActs()
    {
        using var capture = new TextCaptureOperation(); using var run = new OcrFollowUp(OcrOptions);
        CapturedText? result = null; var request = new TaskCompletionSource<CapturedText>(); var calls = 0;
        var task = capture.RunAsync(_ => request.Task, text => { Assert.True(capture.IsActive); result = text; }, _ => Assert.Fail());
        request.SetResult(new("fixture", TextSource.ScreenOcr)); await task;
        Assert.False(capture.IsActive);
        await run.RunAsync(result!, () => !capture.IsActive, (_, _, _) => Task.CompletedTask, (_, _, _) => Assert.Fail(), _ => ++calls);
        var late = new TaskCompletionSource<CapturedText>();
        var old = capture.RunAsync(_ => late.Task, _ => ++calls, _ => Assert.Fail());
        await capture.RunAsync(_ => Task.FromResult(new CapturedText("new", TextSource.ScreenOcr)), _ => { }, _ => Assert.Fail());
        late.SetResult(new("late", TextSource.ScreenOcr)); await old; Assert.Equal(1, calls);
    }
    [Fact] public async Task OcrSourceIsIndependentAndAutomaticTtsRequiresConsentWhileManualReadStillWorks()
    {
        var requests = 0; var stops = 0; var reads = 0;
        using var session = new QuestionSession((_, _, _, _, _) => { ++requests; return Task.FromResult("answer"); }, (_, _) => { ++reads; return Task.CompletedTask; }, () => ++stops);
        var s = OcrOptions with { Provider = TtsProvider.MiMo, AllowAutomaticMiMo = false };
        await session.AskAsync("fixture", s, "fixture-key", QuestionSource.OcrAutomatic);
        Assert.Equal(1, requests); Assert.Equal(0, reads); Assert.Equal(QuestionSource.OcrAutomatic, session.Source);
        var before = stops;
        await session.AskAsync("unauthorized", s with { AllowOcrLlm = false, AutoAsk = true, AllowAutomaticLlm = true, LlmAuthorizedBaseUrl = s.LlmBaseUrl }, "fixture-key", QuestionSource.OcrAutomatic);
        await session.AskAsync("mouse", s, "fixture-key", QuestionSource.MouseAutomatic);
        Assert.Equal(1, requests); Assert.Equal(before, stops);
        await session.ReadAnswerAsync(s); Assert.Equal(1, reads);
        await session.AskAsync("manual", s with { AllowOcrLlm = false, LlmAutoRead = false }, "fixture-key"); Assert.Equal(2, requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OcrQuestionStopAndSourceSpecificCancellationBlockLateSpeech(bool stop)
    {
        var complete = new TaskCompletionSource<string>(); var reads = 0; CancellationToken token = default;
        using var session = new QuestionSession((_, _, _, _, t) => { token = t; return complete.Task; }, (_, _) => { ++reads; return Task.CompletedTask; }, () => { });
        var running = session.AskAsync("fixture", OcrOptions, "fixture-key", QuestionSource.OcrAutomatic);
        session.CancelMouseAutomatic(); Assert.False(token.IsCancellationRequested);
        if (stop) session.Cancel(); else session.CancelOcr();
        Assert.True(token.IsCancellationRequested); complete.SetResult("late"); await running; Assert.Equal(0, reads);
    }
    [Fact] public void QuickSettingComparisonRejectsAnyOtherBusinessChange()
    {
        var s = OcrOptions; var quick = s with { LlmAutoRead = !s.LlmAutoRead };
        Assert.True(SeleVox.App.App.OnlyLlmAutoReadChanged(s, quick));
        Assert.False(SeleVox.App.App.OnlyLlmAutoReadChanged(s, quick with { StartWithWindows = true }));
        Assert.False(SeleVox.App.App.OnlyLlmAutoReadChanged(s, quick with { OcrAction = OcrCompletionAction.Read }));
        Assert.False(SeleVox.App.App.OnlyLlmAutoReadChanged(s, quick with { AllowOcrLlm = false }));
    }
    [Theory] [InlineData(TtsProvider.Azure)] [InlineData(TtsProvider.Edge)] [InlineData(TtsProvider.MiMo)] [InlineData(TtsProvider.OpenAiCompatible)]
    public void UnauthorizedOnlineReadFailsBeforeCredentialsOrDevices(TtsProvider provider)
    {
        var error = Assert.Throws<UserFeedbackException>(() => SpeechAudio.PrecheckAutomatic("fixture", new(Provider: provider)));
        Assert.Contains("授权", error.Message);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OcrIncrementalSpeechHonorsOnlineTtsConsent(bool authorized)
    {
        var reads = 0;
        using var session = new QuestionSession((_, _, _, delta, _) => { delta("First sentence. "); return Task.FromResult("First sentence. Second sentence."); },
            (_, _) => { ++reads; return Task.CompletedTask; }, () => { }, (_, _, _) => { ++reads; return Task.CompletedTask; });
        var s = OcrOptions with { Provider = TtsProvider.MiMo, AllowAutomaticMiMo = authorized, LlmStreaming = true, LlmReadingStrategy = AnswerReadingStrategy.Incremental };
        await session.AskAsync("fixture", s, "fixture-key", QuestionSource.OcrAutomatic);
        Assert.Equal(authorized, reads > 0); Assert.True(session.Snapshot.AnswerComplete);
    }

    [Fact] public async Task QuickAutoReadToggleDoesNotInvalidatePendingOcrRead()
    {
        var snapshot = OcrOptions with { OcrAction = OcrCompletionAction.Read };
        var saved = snapshot; var calls = 0;
        using var run = new OcrFollowUp(snapshot);
        await run.RunAsync(new("fixture", TextSource.ScreenOcr), () => SeleVox.App.App.OnlyLlmAutoReadChanged(snapshot, saved),
            (_, _, _) => { saved = saved with { LlmAutoRead = !saved.LlmAutoRead }; return Task.CompletedTask; },
            (_, _, _) => ++calls, _ => Assert.Fail());
        Assert.Equal(1, calls);
    }

}
