using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class Pr9Tests
{
    internal static Settings Incremental => ChatCompletionTests.Options with { LlmReadingStrategy = AnswerReadingStrategy.Incremental };
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    [Fact] public void SentenceFragmentsUnicodeParagraphsAndBoundedLongTail()
    {
        var buffer = new AnswerSentenceBuffer();
        Assert.Empty(buffer.Append("中文\uD83C"));
        Assert.Equal(new[] { "中文🌟。" }, buffer.Append("\uDF1F。"));
        Assert.Empty(buffer.Append("Value 3."));
        Assert.Equal(new[] { "Value 3.14!", "Next?", "段落" }, buffer.Append("14! Next?\n段落\n"));
        var longText = string.Concat(Enumerable.Repeat("字🌟e\u0301", 400));
        var pieces = buffer.Append(longText).Concat(buffer.Append("", true)).ToArray();
        Assert.Equal(longText, string.Concat(pieces));
        Assert.All(pieces, p => { Assert.True(p.Length <= AnswerSentenceBuffer.MaxSegmentLength); Assert.False(char.IsHighSurrogate(p[^1])); Assert.False(char.IsLowSurrogate(p[0])); Assert.NotEqual('\u0301', p[0]); });
        Assert.Empty(buffer.Append("Dr."));
        Assert.Equal(new[] { "Dr. Smith arrived." }, buffer.Append(" Smith arrived."));
    }
    [Theory]
    [InlineData(ThinkingProtocol.None, ThinkingOption.Default, null)]
    [InlineData(ThinkingProtocol.OpenAi, ThinkingOption.Default, null)]
    [InlineData(ThinkingProtocol.OpenAi, ThinkingOption.Disabled, "none")]
    [InlineData(ThinkingProtocol.OpenAi, ThinkingOption.Low, "low")]
    [InlineData(ThinkingProtocol.OpenAi, ThinkingOption.Medium, "medium")]
    [InlineData(ThinkingProtocol.OpenAi, ThinkingOption.High, "high")]
    [InlineData(ThinkingProtocol.MiMo, ThinkingOption.Default, null)]
    [InlineData(ThinkingProtocol.MiMo, ThinkingOption.Disabled, "disabled")]
    [InlineData(ThinkingProtocol.MiMo, ThinkingOption.Enabled, "enabled")]
    public async Task ThinkingFieldsAreExplicitAndRejectedOnce(ThinkingProtocol protocol, ThinkingOption option, string? value)
    {
        var calls = 0;
        using var http = new HttpClient(new ChatCompletionTests.Handler(async request =>
        {
            calls++; using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            if (value is null) { Assert.False(body.TryGetProperty("reasoning_effort", out _)); Assert.False(body.TryGetProperty("thinking", out _)); }
            else if (protocol == ThinkingProtocol.OpenAi) { Assert.Equal(value, body.GetProperty("reasoning_effort").GetString()); Assert.False(body.TryGetProperty("thinking", out _)); }
            else { Assert.Equal(value, body.GetProperty("thinking").GetProperty("type").GetString()); Assert.False(body.TryGetProperty("reasoning_effort", out _)); }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient(http).GenerateAsync("fixture", Incremental with { LlmThinkingProtocol = protocol, LlmThinking = option }, "fake", _ => { }, default));
        Assert.Contains("Thinking", error.Message); Assert.Equal(1, calls);
    }
    [Fact] public void OldConfigurationAndFreePromptRoundTrip()
    {
        var old = JsonSerializer.Deserialize<Settings>("{\"LlmSystemPrompt\":\"自由 prompt\\n完整保留\",\"Applications\":[{\"ExecutablePath\":\"C:\\\\Tools\\\\editor.exe\"}]}")!;
        Assert.False(old.AutoAsk); Assert.False(old.AllowAutomaticLlm); Assert.False(old.Applications![0].AutoAsk);
        Assert.Equal(AnswerReadingStrategy.Complete, old.LlmReadingStrategy);
        var custom = new PromptPreset("custom", "我的", "自定义内容");
        var next = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(old with { CustomPrompts = [custom], SelectedPromptId = "custom" }))!;
        Assert.Equal(old.LlmSystemPrompt, next.LlmSystemPrompt); Assert.Equal(custom, Assert.Single(next.CustomPrompts!));
        Assert.Equal(4, PromptPresets.BuiltIn.Count); Assert.True(next.IsValid);
    }
    [Fact] public async Task AutomaticPermissionPrecedesCancellationAndManualIsIndependent()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var stops = 0; var reads = 0;
        using var qa = new QuestionSession((_, _, _, _, _) => { calls++; return pending.Task; }, (_, _) => { reads++; return Task.CompletedTask; }, () => stops++);
        var manual = qa.AskAsync("manual", Incremental, "fake"); var before = stops;
        await qa.AskAsync("auto", Incremental with { AutoAsk = true }, "fake", true);
        qa.CancelAutomatic(); Assert.Equal(before, stops); Assert.Equal(1, calls); Assert.Equal("manual", qa.Snapshot.Question);
        pending.SetResult("answer"); await manual; Assert.Equal(1, reads);
        var allowed = Incremental with { AutoAsk = true, AllowAutomaticLlm = true, LlmAuthorizedBaseUrl = Settings.NormalizeServiceUrl(Incremental.LlmBaseUrl), Provider = TtsProvider.MiMo };
        await qa.AskAsync("auto", allowed, "fake", true); Assert.Equal(1, reads); // no automatic MiMo permission
        Assert.False((allowed with { LlmBaseUrl = "https://different.example/v1" }).AllowsAutomaticLlm);
        var path = @"C:\Tools\editor.exe";
        allowed = allowed with { Applications = [new(path, true, true)] };
        Assert.True(allowed.AllowsAutomaticAsk(path.ToUpperInvariant())); Assert.False(allowed.AllowsAutomaticAsk(@"C:\Other\editor.exe"));
    }
    [Fact] public async Task QueueWaitsForRealFinishedSignalPreservesOrderAndPause()
    {
        var release = Signal(); var first = Signal();
        var backend = new ControlledAudio(); using var reading = new ReadingSession(backend, backend);
        using var qa = new QuestionSession(async (_, _, _, delta, token) =>
        { delta("第一句。第二句。余文"); first.SetResult(); await release.Task.WaitAsync(token); return "第一句。第二句。余文"; },
            (_, _) => throw new Exception("must not read whole answer"), reading.Stop, reading.ReadSentenceAsync);
        var task = qa.AskAsync("fixture", Incremental, "fake");
        await first.Task; await Until(() => backend.Players.Count == 1);
        Assert.False(task.IsCompleted); Assert.Equal(new[] { "第一句。" }, backend.Text.ToArray());
        reading.TogglePause(); Assert.Equal(ReadingState.Paused, reading.Status.State); await Task.Delay(60); Assert.Single(backend.Text);
        reading.TogglePause(); backend.Players.ElementAt(0).Finish(); await Until(() => backend.Players.Count == 2);
        Assert.Equal(new[] { "第一句。", "第二句。" }, backend.Text.ToArray());
        release.SetResult(); backend.Players.ElementAt(1).Finish(); await Until(() => backend.Players.Count == 3);
        Assert.False(task.IsCompleted); backend.Players.ElementAt(2).Finish(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "第一句。", "第二句。", "余文" }, backend.Text.ToArray()); Assert.Equal(QuestionState.Completed, qa.Snapshot.State);
        Assert.All(backend.Players, p => Assert.True(p.Disposed));
    }
    [Fact] public async Task BoundedQueueBackpressureAndCancelDiscardPendingSentences()
    {
        var generated = 0; var backend = new ControlledAudio(); using var reading = new ReadingSession(backend, backend);
        using var qa = new QuestionSession(async (_, _, _, delta, token) =>
        { for (var i = 0; i < 100; i++) { delta($"句子{i}。"); Interlocked.Increment(ref generated); } await Task.Delay(Timeout.Infinite, token); return ""; },
            (_, _) => Task.CompletedTask, reading.Stop, reading.ReadSentenceAsync);
        var task = qa.AskAsync("fixture", Incremental, "fake"); await Until(() => backend.Players.Count == 1);
        await Task.Delay(100); Assert.InRange(generated, 1, 9); Assert.Single(backend.Text);
        qa.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(QuestionState.Cancelled, qa.Snapshot.State);
        backend.Players.First().Finish(); Assert.Single(backend.Text); Assert.True(backend.Players.First().Disposed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StreamFailureOrTtsFailureKeepsAnswerWithoutReplaying(bool ttsFails)
    {
        var release = Signal(); var backend = new ControlledAudio(); using var reading = new ReadingSession(backend, backend);
        using var qa = new QuestionSession(async (_, _, _, delta, token) =>
        { delta("已完成一句。未完成余文"); await release.Task.WaitAsync(token); if (!ttsFails) throw new IOException(); return "已完成一句。未完成余文"; },
            (_, _) => Task.CompletedTask, reading.Stop, reading.ReadSentenceAsync);
        var task = qa.AskAsync("fixture", Incremental, "fake"); await Until(() => backend.Players.Count == 1);
        if (ttsFails) { backend.Players.First().Finish(new IOException()); await Until(() => reading.Status.State == ReadingState.Error); }
        release.SetResult(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(backend.Text); Assert.Contains("未完成余文", qa.Snapshot.Answer); Assert.Contains("不完整", qa.Snapshot.Message);
        Assert.Equal(ttsFails ? QuestionState.Completed : QuestionState.Error, qa.Snapshot.State);
    }
    internal sealed class ControlledAudio : ISpeechSynthesis, IAudioPlaybackFactory
    {
        internal ConcurrentQueue<string> Text = new(); internal ConcurrentQueue<Player> Players = new();
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation) { Text.Enqueue(text); return Task.FromResult<Stream>(new MemoryStream()); }
        public IAudioPlayback CreatePlayback(Stream audio, Settings settings) { var p = new Player(audio); Players.Enqueue(p); return p; }
    }
    [Fact] public async Task NewQuestionCancelsPausedQueueAndLateOldOutputCannotReturn()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); Action<string>? oldDelta = null;
        var backend = new ControlledAudio(); using var reading = new ReadingSession(backend, backend);
        using var qa = new QuestionSession((text, _, _, delta, _) =>
        {
            if (text == "old") { oldDelta = delta; delta("旧句。旧余文"); return release.Task; }
            delta("新句。"); return Task.FromResult("新句。");
        }, (_, _) => Task.CompletedTask, reading.Stop, reading.ReadSentenceAsync);
        var old = qa.AskAsync("old", Incremental, "fake"); await Until(() => backend.Players.Count == 1);
        reading.TogglePause(); var next = qa.AskAsync("new", Incremental, "fake"); await Until(() => backend.Players.Count == 2);
        Assert.True(backend.Players.First().Disposed); oldDelta!("迟到句。"); release.SetResult("旧句。旧余文迟到句。");
        await old; backend.Players.Last().Finish(); await next;
        Assert.Equal(new[] { "旧句。", "新句。" }, backend.Text.ToArray()); Assert.Equal("新句。", qa.Snapshot.Answer);
    }
    [Fact] public async Task NonStreamingFallsBackToWholeAnswerAndAutomaticRevokeDoesNotStopManualReplay()
    {
        var fullReads = 0; var sentences = 0; var stops = 0;
        using var qa = new QuestionSession((_, _, _, delta, _) => { delta("答复。"); return Task.FromResult("答复。"); },
            (_, _) => { fullReads++; return Task.CompletedTask; }, () => stops++, (_, _, _) => { sentences++; return Task.CompletedTask; });
        var options = Incremental with { LlmStreaming = false, AutoAsk = true, AllowAutomaticLlm = true, LlmAuthorizedBaseUrl = Incremental.LlmBaseUrl };
        await qa.AskAsync("auto", options, "fake", true); Assert.Equal(1, fullReads); Assert.Equal(0, sentences);
        await qa.ReadAnswerAsync(options); var before = stops; qa.CancelAutomatic(); Assert.Equal(before, stops);
        await Assert.ThrowsAsync<UserFeedbackException>(() => qa.AskAsync("bad", options with { LlmThinkingProtocol = ThinkingProtocol.MiMo, LlmThinking = ThinkingOption.High }, "fake"));
        Assert.Equal(before, stops);
    }
    internal sealed class Player(Stream audio) : IAudioPlayback
    {
        public event Action<Exception?>? Finished;
        public bool Disposed;
        public void Play() { } public void Pause() { } public void Stop() { }
        public void Dispose() { Disposed = true; audio.Dispose(); }
        public void Finish(Exception? error = null) => Finished?.Invoke(error);
    }
}
