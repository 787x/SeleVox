using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class CreativeTextTests
{
    [Theory]
    [InlineData("你好。\r\n[叹气]再见！", "[小声]你好。[停顿]\r\n[叹气]再见！", true)]
    [InlineData("[停顿]a", "[停顿][停顿]a", true)]
    [InlineData("你好。", "你好！", false)]
    [InlineData("你好。", "[非法]你好。", false)]
    [InlineData("你好。", "你好", false)]
    [InlineData("a\r\nb", "a\nb", false)]
    [InlineData("[叹气]a", "[叹[停顿]气]a", false)]
    [InlineData("a[原有]b", "[停顿]a[原有][小声]b", true)]
    [InlineData("你好。", "", false)]
    public void OnlyLegalInsertionsPreserveOriginal(string source, string output, bool valid) => Assert.Equal(valid, CreativeText.OnlyInsertsTags(source, output));
    [Fact] public void TagsOnlyInsertAtWholeTextElementAndBracketBoundaries()
    {
        (string Source, int[] Boundaries)[] cases =
        [
            ("\U0001F600", [0, 2]),                          // Surrogate pair.
            ("a\u0301\u0327", [0, 3]),                     // Base plus combining marks.
            ("a\r\nb", [0, 1, 3, 4]),                      // CRLF is one text element.
            ("\U0001F469\u200D\U0001F4BB", [0, 5]),        // Joined emoji.
            ("\U0001F44D\U0001F3FD", [0, 4]),              // Emoji plus skin tone.
            ("\U0001F1E8\U0001F1F3", [0, 4]),              // Flag regional indicators.
            ("你好。", [0, 1, 2, 3]),
            ("[停顿]", [0, 4]),
            ("[原有\U0001F600]a\u0301", [0, 6, 8]),
            ("[原有]\u0301a", [0, 5, 6]),                  // Closing bracket plus mark.
            ("[\u0301", [0, 2])                           // Unclosed bracket plus mark.
        ];
        foreach (var (source, boundaries) in cases)
            foreach (var tag in MiMoCreative.AudioTags)
                for (var position = 0; position <= source.Length; ++position)
                {
                    var output = source.Insert(position, "[" + tag + "][" + tag + "]");
                    var valid = boundaries.Contains(position);
                    Assert.Equal((source, position, valid), (source, position, CreativeText.OnlyInsertsTags(source, output)));
                    if (valid) CreativeText.Validate(CreativePurpose.AudioTags, source, output);
                    else Assert.Throws<UserFeedbackException>(() => CreativeText.Validate(CreativePurpose.AudioTags, source, output));
                }
    }
    [Fact] public void UnicodeOriginalRemainsExactWithMultipleLegalInsertions()
    {
        const string source = "\U0001F600a\u0301\r\n[叹气]再见。";
        const string output = "[小声]\U0001F600[停顿]a\u0301[吸气]\r\n[叹气][叹气]再见。[停顿]";
        Assert.True(CreativeText.OnlyInsertsTags("[停顿]\u0301a", "[停顿][停顿][停顿]\u0301a"));
        Assert.True(CreativeText.OnlyInsertsTags(source, source));
        Assert.True(CreativeText.OnlyInsertsTags(source, output));
        Assert.False(CreativeText.OnlyInsertsTags(source, output.Replace("a\u0301", "\u00E1", StringComparison.Ordinal)));
        Assert.False(CreativeText.OnlyInsertsTags(source, output.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }
    [Theory] [InlineData(CreativePurpose.VoiceDescription, 1000)] [InlineData(CreativePurpose.Style, 1000)] [InlineData(CreativePurpose.Script, 32768)] [InlineData(CreativePurpose.AudioTags, 32768)]
    internal void Limits(CreativePurpose purpose, int limit)
    {
        CreativeText.Validate(purpose, new string('a', limit), new string('a', limit));
        Assert.Throws<UserFeedbackException>(() => CreativeText.Validate(purpose, "a", new string('a', limit + 1)));
        Assert.Throws<UserFeedbackException>(() => CreativeText.Validate(purpose, "a", " "));
        Assert.Throws<UserFeedbackException>(() => CreativeText.Validate(purpose, "a", "```text```"));
    }
}

[Collection("WPF desktop")]
public sealed class CreativeWindowTests
{
    private static T Find<T>(AudioWorkbenchWindow w, string name) => (T)w.FindName(name);
    private static HttpResponseMessage Response(string output) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = output } } } }), Encoding.UTF8, "application/json") };
    [Fact] public Task RequestsPromptsReplacementRecoveryAndWavIsolation() => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var settings = new Settings(Provider: TtsProvider.MiMo, LlmBaseUrl: "http://localhost:8472/v1", LlmModel: "fixture-model", LlmSystemPrompt: "daily prompt", LlmStreaming: false, LlmThinkingProtocol: ThinkingProtocol.MiMo, LlmThinking: ThinkingOption.Enabled);
        var prompts = new List<string>(); var inputs = new List<string>(); string answer = "生成结果"; var plays = 0;
        using var http = new HttpClient(new MiMoCreativeTests.Handler(async (request, token) => {
            Assert.Equal("http://localhost:8472/v1/chat/completions", request.RequestUri!.ToString()); Assert.Equal("fixture-key", request.Headers.Authorization!.Parameter);
            using var j = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = j.RootElement;
            Assert.Equal("fixture-model", root.GetProperty("model").GetString()); Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
            prompts.Add(root.GetProperty("messages")[0].GetProperty("content").GetString()!); inputs.Add(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
            return Response(answer);
        }));
        var speech = new AudioGenerationTests.Speech();
        var w = new AudioWorkbenchWindow(() => settings, () => { }, (_, _) => { ++plays; return Task.CompletedTask; }, () => { }, () => { }, Path.Combine(folder, "workbench.json"), speech);
        w.ConfigureCreativeClient(new(http), url => { Assert.Equal(settings.LlmBaseUrl, url); return "fixture-key"; });
        try
        {
            w.Show(); w.Width = 900; w.Height = 620;
            var input = Find<TextBox>(w, "InputText"); input.Text = "固定试音"; await w.GenerateAsync(false); var summary = Find<TextBox>(w, "ResultSummary").Text;
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign;
            Find<TextBox>(w, "VoiceDescription").Text = "儒雅老人"; await w.GenerateCreativeAsync(CreativePurpose.VoiceDescription);
            Assert.Equal(answer, Find<TextBox>(w, "VoiceDescription").Text); Assert.Equal(CreativeText.VoicePrompt, prompts[^1]); Assert.Equal("儒雅老人", inputs[^1]);
            Find<Button>(w, "RestoreAiButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("儒雅老人", Find<TextBox>(w, "VoiceDescription").Text);
            Find<TextBox>(w, "MiMoStyle").Text = "温柔"; await w.GenerateCreativeAsync(CreativePurpose.Style); Assert.Equal(answer, Find<TextBox>(w, "MiMoStyle").Text);
            Assert.Contains("user.content", prompts[^1]);
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.PresetVoice;
            Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Singing;
            input.Text = "写一首春天的歌"; await w.GenerateCreativeAsync(CreativePurpose.Script); Assert.Contains("歌词本身", prompts[^1]);
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceClone; input.Text = "春天的介绍"; await w.GenerateCreativeAsync(CreativePurpose.Script); Assert.Contains("播报稿", prompts[^1]);
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.PresetVoice;
            Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.AudioTags;
            input.Text = "你好。\r\n再见。"; answer = "[小声]你好。\r\n[停顿]再见。"; await w.GenerateCreativeAsync(CreativePurpose.AudioTags);
            Assert.Equal(answer, input.Text); Assert.Contains(answer.Replace("\r\n", "\n"), Find<TextBox>(w, "RequestPreview").Text.Replace("\r\n", "\n"));
            var valid = input.Text; answer = "改写"; await w.GenerateCreativeAsync(CreativePurpose.AudioTags); Assert.Equal(valid, input.Text);
            Assert.Contains("非法", Find<TextBlock>(w, "AiStatus").Text);
            foreach (var provider in Enum.GetValues<TtsProvider>()) { Find<ComboBox>(w, "Provider").SelectedItem = provider; input.Text = "写一段介绍"; answer = "介绍正文"; await w.GenerateCreativeAsync(CreativePurpose.Script); Assert.Equal(answer, input.Text); Assert.Contains("播报稿", prompts[^1]); }
            Assert.Single(speech.Texts); Assert.Equal(summary, Find<TextBox>(w, "ResultSummary").Text); Assert.True(Find<Button>(w, "SaveAudio").IsEnabled); await w.PreviewAsync(); Assert.Equal(1, plays);
            Assert.Equal("daily prompt", settings.LlmSystemPrompt);
            Find<ComboBox>(w, "Provider").SelectedItem = TtsProvider.MiMo; Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign;
            w.UpdateLayout();
            foreach (var name in new[] { "AiVoice", "AiStyle", "AiScript" }) { var button = Find<Button>(w, name); Assert.True(button.ActualWidth > 0); var panel = (FrameworkElement)button.Parent; Assert.True(button.TranslatePoint(new Point(button.ActualWidth, 0), panel).X <= panel.ActualWidth + 1); }
            OcrDesktopTests.RenderIfRequested(w, "pr13-minimum");
        }
        finally { w.Close(); Directory.Delete(folder, true); }
    });
    [Fact] public Task RealLoopbackUsesCreativePromptAndSavedOptions() => OcrDesktopTests.RunSta(async () =>
    {
        using var server = new ChatNetworkTests.Server();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var serving = Task.Run(async () =>
        {
            var context = await server.Next(); Assert.Equal("/v1/chat/completions", context.Request.Url!.AbsolutePath);
            Assert.Equal("Bearer fixture-key", context.Request.Headers["Authorization"]);
            using var reader = new StreamReader(context.Request.InputStream); using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
            var root = json.RootElement; Assert.Equal("fixture-model", root.GetProperty("model").GetString());
            Assert.Equal("low", root.GetProperty("reasoning_effort").GetString()); Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal("春天", root.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Contains("播报稿", root.GetProperty("messages")[0].GetProperty("content").GetString());
            context.Response.ContentType = "text/event-stream";
            await ChatNetworkTests.Server.Write(context.Response, "data: {\"choices\":[{\"delta\":{\"content\":\"春天来了。\"}}]}\n\ndata: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{}}]}\n\ndata: [DONE]\n\n"); context.Response.Close();
        });
        var w = new AudioWorkbenchWindow(() => new(LlmBaseUrl: server.Address + "/v1", LlmModel: "fixture-model", LlmThinkingProtocol: ThinkingProtocol.OpenAi, LlmThinking: ThinkingOption.Low), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, Path.Combine(folder, "workbench.json"));
        w.ConfigureCreativeClient(new(), _ => "fixture-key");
        try { w.Show(); var input = Find<TextBox>(w, "InputText"); await w.GenerateCreativeAsync(CreativePurpose.Script); Assert.Contains("先输入", Find<TextBlock>(w, "AiStatus").Text); input.Text = "春天"; await w.GenerateCreativeAsync(CreativePurpose.Script); await serving; Assert.Equal("春天来了。", input.Text); }
        finally { w.Close(); Directory.Delete(folder, true); }
    });
    [Theory] [InlineData("edit")] [InlineData("provider")] [InlineData("mode")] [InlineData("control")] [InlineData("cancel")] [InlineData("close")] [InlineData("new")]
    public Task LateResultsNeverOverwriteDraft(string action) => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var http = new HttpClient(new MiMoCreativeTests.Handler(async (_, _) => { if (++calls == 1) { entered.SetResult(); await release.Task; } return Response(calls == 1 ? "旧结果" : "新结果"); }));
        var w = new AudioWorkbenchWindow(() => new(Provider: TtsProvider.MiMo, LlmModel: "fixture", LlmStreaming: false), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, Path.Combine(folder, "workbench.json"));
        w.ConfigureCreativeClient(new(http), _ => "fixture");
        try
        {
            w.Show(); var input = Find<TextBox>(w, "InputText"); input.Text = "原需求"; var pending = w.GenerateCreativeAsync(CreativePurpose.Script); await entered.Task;
            switch (action) { case "edit": input.Text = "编辑草稿"; break; case "provider": Find<ComboBox>(w, "Provider").SelectedItem = TtsProvider.Windows; break; case "mode": Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign; break; case "control": Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Singing; break; case "cancel": w.CancelAi(); break; case "close": w.Close(); break; case "new": await w.GenerateCreativeAsync(CreativePurpose.Script); break; }
            var expected = input.Text; release.SetResult(); await pending; Assert.Equal(expected, input.Text);
        }
        finally { release.TrySetResult(); w.Close(); Directory.Delete(folder, true); }
    });
    [Theory] [InlineData("empty")] [InlineData("long")] [InlineData("sse")] [InlineData("error")] [InlineData("key")] [InlineData("model")]
    public Task FailurePreservesInput(string failure) => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder); var calls = 0;
        using var http = new HttpClient(new MiMoCreativeTests.Handler((_, _) => { ++calls; return Task.FromResult(failure switch { "sse" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"部分\"}}]}\n\n", Encoding.UTF8, "text/event-stream") }, "error" => new HttpResponseMessage(HttpStatusCode.BadRequest), _ => Response(failure == "long" ? new string('a', 1001) : "") }); }));
        var w = new AudioWorkbenchWindow(() => new(Provider: TtsProvider.MiMo, LlmModel: failure == "model" ? "" : "fixture"), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, Path.Combine(folder, "workbench.json")); w.ConfigureCreativeClient(new(http), _ => failure == "key" ? null : "fixture");
        try { w.Show(); Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign; var input = Find<TextBox>(w, "VoiceDescription"); input.Text = "原需求"; await w.GenerateCreativeAsync(CreativePurpose.VoiceDescription); Assert.Equal("原需求", input.Text); Assert.False(Find<Button>(w, "CancelAiButton").IsEnabled); if (failure is "key" or "model") Assert.Equal(0, calls); else Assert.Equal(1, calls); }
        finally { w.Close(); Directory.Delete(folder, true); }
    });
}
