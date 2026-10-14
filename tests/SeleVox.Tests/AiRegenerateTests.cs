using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class AiRegenerateTests
{
    [Fact] public Task TimerPreservesDraftFocusSelectionAndCaptureRestoreUntilAcceptedExternalAsk() => OcrDesktopTests.RunSta(async () =>
    {
        using var session = new QuestionSession((_, _, _, _, _) => Task.FromResult("answer"), (_, _) => Task.CompletedTask, () => { });
        var options = ChatCompletionTests.Options with { LlmAutoRead = false };
        await session.AskAsync("same question", options, "key");
        var window = new AiAnswerWindow(session, () => options, () => "idle", session.Cancel, () => { }, _ => { });
        try
        {
            window.ShowAnswer(); var editor = Editor(window);
            Assert.False(editor.IsReadOnly); Assert.True(editor.AcceptsReturn);
            editor.Text = "修改中的问题\nsecond line"; editor.Focus(); editor.Select(2, 3);
            await Task.Delay(350); // Actual DispatcherTimer ticks while the editor has keyboard focus.
            Assert.Equal("修改中的问题\nsecond line", editor.Text); Assert.True(editor.IsKeyboardFocused);
            Assert.Equal(2, editor.SelectionStart); Assert.Equal(3, editor.SelectionLength);
            Assert.Contains(AiWindowTests.Descendants<TextBlock>(window), b => b.Text.Contains("问题已修改"));
            Assert.Equal("same question", session.Snapshot.Question); Assert.Equal("answer", session.Snapshot.Answer);
            session.Cancel(); await session.ReadAnswerAsync(options); window.Refresh();
            Assert.Equal("修改中的问题\nsecond line", editor.Text);
            window.HideForCapture(); window.RestoreAfterCapture(); window.Refresh();
            Assert.Equal("修改中的问题\nsecond line", editor.Text);
            var id = session.Snapshot.RequestId;
            await session.AskAsync("same question", options, "key"); window.ShowAnswer(false);
            Assert.True(session.Snapshot.RequestId > id); Assert.Equal("same question", editor.Text);
            await session.AskAsync("external OCR question", options, "key"); window.Refresh();
            Assert.Equal("external OCR question", editor.Text);
            OcrDesktopTests.RenderIfRequested(window, "ai-regenerate-editor");
        }
        finally { window.Close(); }
    });

    [Theory] [InlineData(false)] [InlineData(true)]
    public Task ButtonSendsEditedQuestionAndCurrentOptionsOverHttpWithManualSemantics(bool incremental) => OcrDesktopTests.RunSta(async () =>
    {
        using var server = new ChatNetworkTests.Server();
        var saved = ChatCompletionTests.Options with { LlmBaseUrl = server.Address, AutoAsk = true, AllowAutomaticLlm = true, LlmAuthorizedBaseUrl = Settings.NormalizeServiceUrl(server.Address), LlmAutoRead = false };
        Assert.True(saved.AllowsAutomaticLlm);
        var reads = new List<string>();
        using var session = new QuestionSession(new ChatCompletionClient().GenerateAsync, (text, _) => { reads.Add(text); return Task.CompletedTask; }, () => { },
            (text, _, _) => { reads.Add(text); return Task.CompletedTask; });
        var automaticServer = Reply(server, "original", saved, "original answer");
        await session.AskAsync("original", saved, "fake-key", true); await automaticServer;
        Task? request = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new AiAnswerWindow(session, () => saved, () => "idle", session.Cancel, () => { }, text => request = session.AskAsync(text, saved, "fake-key"));
        try
        {
            window.ShowAnswer(false); Editor(window).Text = "edited\nquestion";
            saved = saved with { AutoAsk = false, LlmAuthorizedBaseUrl = null, LlmModel = "current-model", LlmSystemPrompt = "current prompt", LlmThinkingProtocol = ThinkingProtocol.OpenAi, LlmThinking = ThinkingOption.Low, LlmAutoRead = true,
                LlmReadingStrategy = incremental ? AnswerReadingStrategy.Incremental : AnswerReadingStrategy.Complete };
            Assert.False(saved.AllowsAutomaticLlm);
            var serving = Reply(server, "edited\nquestion", saved, "new answer", release.Task);
            Click(window); Assert.NotNull(request); Assert.Equal("edited\nquestion", session.Snapshot.Question);
            Assert.NotEqual("original answer", session.Snapshot.Answer);
            await Pr9Tests.Until(() => session.Snapshot.Answer == "new answer"); window.Refresh();
            Assert.Equal(QuestionState.Generating, session.Snapshot.State);
            Assert.Contains(AiWindowTests.Descendants<TextBox>(window), b => b.IsReadOnly && b.Text == "new answer");
            Assert.False(request!.IsCompleted); release.SetResult();
            await request!.WaitAsync(TimeSpan.FromSeconds(5)); await serving; window.Refresh();
            Assert.Equal("new answer", session.Snapshot.Answer); Assert.Equal(new[] { "new answer" }, reads);
            Editor(window).Text = "next draft"; window.Refresh();
            Assert.Contains(AiWindowTests.Descendants<TextBlock>(window), b => b.Text.Contains("仍对应上次提交"));
            Assert.Equal("edited\nquestion", session.Snapshot.Question);
        }
        finally { release.TrySetResult(); window.Close(); if (request is not null) await request; }
    });

    [Fact] public Task InvalidDraftAndDoubleClickPreserveOldSessionThenValidReplacementRejectsLateEvents() => OcrDesktopTests.RunSta(async () =>
    {
        var results = new List<TaskCompletionSource<string>>(); var deltas = new List<Action<string>>(); var tokens = new List<CancellationToken>(); var reads = new List<string>(); var stops = 0;
        var saved = ChatCompletionTests.Options; var key = "key"; var attempts = 0; Task? request = null;
        using var session = new QuestionSession((_, _, _, delta, token) =>
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            results.Add(result); deltas.Add(delta); tokens.Add(token); return result.Task;
        }, (text, _) => { reads.Add(text); return Task.CompletedTask; }, () => ++stops);
        var original = session.AskAsync("original", saved, key); deltas[0]("old partial");
        AiAnswerWindow? window = null;
        window = new AiAnswerWindow(session, () => saved, () => "playing", session.Cancel, () => { }, text =>
        {
            ++attempts;
            try { ChatCompletionClient.Validate(text, saved, key); request = session.AskAsync(text, saved, key); }
            catch (UserFeedbackException ex) { window!.SetNotice(ex.Message); }
        });
        try
        {
            window.ShowAnswer(); var editor = Editor(window); var before = session.Snapshot; var beforeStops = stops;
            foreach (var invalid in new[] { " ", new string('x', ChatCompletionClient.MaxText + 1), "bad model", "missing key" })
            {
                editor.Text = invalid;
                saved = invalid == "bad model" ? saved with { LlmModel = "" } : ChatCompletionTests.Options;
                key = invalid == "missing key" ? "" : "key";
                Click(window); var count = attempts; Click(window); Assert.Equal(count, attempts);
                Assert.Equal(before, session.Snapshot); Assert.Equal(beforeStops, stops); Assert.False(tokens[0].IsCancellationRequested);
                Assert.Single(results); Assert.Null(request); Assert.Equal(invalid, editor.Text);
                await Task.Delay(650); window.Refresh();
            }
            saved = ChatCompletionTests.Options; key = "key"; editor.Text = "replacement";
            Click(window); Click(window); Assert.Equal(2, results.Count); Assert.True(tokens[0].IsCancellationRequested);
            deltas[0]("late old"); results[0].SetResult("late answer"); await original;
            Assert.Equal("replacement", session.Snapshot.Question); Assert.Equal("", session.Snapshot.Answer); Assert.Empty(reads);
            deltas[1]("new partial"); window.Refresh(); Assert.Contains(AiWindowTests.Descendants<TextBox>(window), b => b.Text == "new partial");
            window.Close(); Assert.True(tokens[1].IsCancellationRequested);
            deltas[1]("late after close"); results[1].SetResult("late after close"); await request!;
            Assert.Equal("new partial", session.Snapshot.Answer); Assert.Empty(reads);
        }
        finally { window.Close(); foreach (var result in results) result.TrySetResult("cleanup"); await original; if (request is not null) await request; }
    });

    private static async Task Reply(ChatNetworkTests.Server server, string expected, Settings options, string answer, Task? release = null)
    {
        var context = await server.Next();
        using var reader = new StreamReader(context.Request.InputStream);
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync()); var root = json.RootElement;
        Assert.Equal(expected, root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(options.LlmSystemPrompt, root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(options.LlmModel, root.GetProperty("model").GetString()); Assert.Equal(options.LlmStreaming, root.GetProperty("stream").GetBoolean());
        if (options.LlmThinking == ThinkingOption.Low) Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
        await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Event(answer));
        if (release is not null) await release;
        await ChatNetworkTests.Server.Write(context.Response, ChatCompletionTests.Finish); context.Response.Close();
    }
    private static TextBox Editor(Window window) => AiWindowTests.Descendants<TextBox>(window).Single(b => !b.IsReadOnly);
    private static void Click(Window window) => AiWindowTests.Descendants<Button>(window).Single(b => b.Content as string == "重新生成").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
