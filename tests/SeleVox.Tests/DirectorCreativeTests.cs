using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class DirectorTextTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("不是 JSON")]
    [InlineData("{\"role\":\"r\",\"scene\":\"s\",\"direction\":\"d\",\"extra\":\"e\"}")]
    [InlineData("{\"role\":\"r\",\"role\":\"r\",\"scene\":\"s\"}")]
    [InlineData("{\"role\":\"r\",\"scene\":\"s\",\"direction\":null}")]
    [InlineData("{\"role\":\"r\",\"scene\":\"s\",\"direction\":\" \"}")]
    [InlineData("{\"role\":\"r\",\"scene\":\"s\",\"direction\":\"\\u0000\"}")]
    [InlineData("{\"role\":\"r\",\"scene\":\"s\",\"direction\":\"```\"}")]
    public void InvalidStructureNeverApplies(string output) => Assert.Throws<UserFeedbackException>(() => CreativeText.ParseDirector(output));
    [Fact] public void CombinedLimitIncludesTitlesAndNewlines()
    {
        // Three titles (3 characters each), two newlines, and the two other fields.
        var valid = JsonSerializer.Serialize(new { role = new string('中', 987), scene = "s", direction = "d" });
        var parsed = CreativeText.ParseDirector(valid); Assert.Equal(1000, parsed.Combined.Length);
        Assert.Equal(987, parsed.Role.Length);
        var invalid = JsonSerializer.Serialize(new { role = new string('中', 988), scene = "s", direction = "d" });
        Assert.Throws<UserFeedbackException>(() => CreativeText.ParseDirector(invalid));
    }
}

[Collection("WPF desktop")]
public sealed class DirectorWindowTests
{
    private static T Find<T>(AudioWorkbenchWindow w, string name) => (T)w.FindName(name);
    private static readonly string Valid = JsonSerializer.Serialize(new { role = "沉稳的电台主持人", scene = "深夜向疲惫的听众道晚安", direction = "放慢语速，气息柔和，句间自然停顿，尾音温暖收束。" });
    private static HttpResponseMessage Response(string value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = value } } } }), Encoding.UTF8, "application/json") };
    [Fact] public Task ThreeFieldsReplaceRestoreAndPreviewInAllMiMoModes() => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var settings = new Settings(Provider: TtsProvider.MiMo, LlmBaseUrl: "http://localhost:8472/v1", LlmModel: "fixture", LlmStreaming: false, LlmSystemPrompt: "daily");
        var calls = 0; var plays = 0; var speech = new AudioGenerationTests.Speech(); string output = Valid;
        using var http = new HttpClient(new MiMoCreativeTests.Handler(async (request, token) =>
        {
            ++calls; using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("http://localhost:8472/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal(CreativeText.Prompt(CreativePurpose.Director, false), json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("角色：主持人\n场景：晚安\n指导：温柔", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            return Response(output);
        }));
        var w = new AudioWorkbenchWindow(() => settings, () => { }, (_, _) => { ++plays; return Task.CompletedTask; }, () => { }, () => { }, Path.Combine(folder, "workbench.json"), speech);
        w.ConfigureCreativeClient(new(http), _ => "fixture-key");
        try
        {
            w.Show(); w.Width = 900; w.Height = 620;
            Find<TextBox>(w, "InputText").Text = "固定试音"; await w.GenerateAsync(false); var summary = Find<TextBox>(w, "ResultSummary").Text;
            foreach (var mode in Enum.GetValues<MiMoCreativeMode>())
            {
                Find<ComboBox>(w, "CreativeMode").SelectedValue = mode;
                Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Director;
                Find<TextBox>(w, "VoiceDescription").Text = "温暖的中年声线";
                var role = Find<TextBox>(w, "DirectorRole"); var scene = Find<TextBox>(w, "DirectorScene"); var direction = Find<TextBox>(w, "DirectorDirection");
                role.Text = " 主持人 "; scene.Text = "晚安"; direction.Text = "温柔";
                await w.GenerateCreativeAsync(CreativePurpose.Director);
                Assert.Equal("沉稳的电台主持人", role.Text); Assert.Equal("深夜向疲惫的听众道晚安", scene.Text);
                Assert.Equal(CreativeText.ParseDirector(Valid).Direction, direction.Text);
                Assert.Contains("角色：沉稳的电台主持人", w.Draft().MiMoStyle); Assert.Contains("场景：深夜", Find<TextBox>(w, "RequestPreview").Text);
                Assert.Equal("固定试音", Find<TextBox>(w, "InputText").Text); Assert.Equal("", Find<TextBox>(w, "MiMoStyle").Text);
                Assert.Equal(summary, Find<TextBox>(w, "ResultSummary").Text); Assert.Contains("输入已修改", Find<TextBlock>(w, "GenerationStatus").Text);
                w.UpdateLayout(); var button = Find<Button>(w, "AiDirector"); Assert.True(button.IsVisible); Assert.True(button.ActualWidth > 0);
                var panel = (FrameworkElement)button.Parent; Assert.True(button.TranslatePoint(new Point(button.ActualWidth, 0), panel).X <= panel.ActualWidth + 1);
                button.BringIntoView(); w.UpdateLayout(); OcrDesktopTests.RenderIfRequested(w, "pr13-director-" + mode);
                direction.Text = "生成后手动编辑";
                Find<Button>(w, "RestoreAiButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(" 主持人 ", role.Text); Assert.Equal("晚安", scene.Text); Assert.Equal("温柔", direction.Text);
                output = "invalid"; await w.GenerateCreativeAsync(CreativePurpose.Director); Assert.Equal(" 主持人 ", role.Text); Assert.Equal("晚安", scene.Text); Assert.Equal("温柔", direction.Text); output = Valid;
            }
            Assert.Equal(6, calls); Assert.Single(speech.Texts); Assert.Equal("daily", settings.LlmSystemPrompt); await w.PreviewAsync(); Assert.Equal(1, plays);
            Find<ComboBox>(w, "Provider").SelectedItem = TtsProvider.Windows; Assert.False(Find<Button>(w, "AiDirector").IsVisible);
            await w.GenerateCreativeAsync(CreativePurpose.Director); Assert.Equal(6, calls);
        }
        finally { w.Close(); Directory.Delete(folder, true); }
    });
    [Theory]
    [InlineData("DirectorRole")] [InlineData("DirectorScene")] [InlineData("DirectorDirection")]
    [InlineData("provider")] [InlineData("mode")] [InlineData("control")] [InlineData("cancel")] [InlineData("close")] [InlineData("new")]
    public Task AnyFieldEditOrLifecycleChangeRejectsWholeLateResult(string action) => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var http = new HttpClient(new MiMoCreativeTests.Handler(async (_, _) => { var call = ++calls; if (call == 1) { entered.SetResult(); await release.Task; } return Response(call == 1 ? JsonSerializer.Serialize(new { role = "旧结果", scene = "旧场景", direction = "旧指导" }) : Valid); }));
        var w = new AudioWorkbenchWindow(() => new(Provider: TtsProvider.MiMo, LlmModel: "fixture", LlmStreaming: false), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, Path.Combine(folder, "workbench.json")); w.ConfigureCreativeClient(new(http), _ => "fixture");
        try
        {
            w.Show(); Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Director;
            await w.GenerateCreativeAsync(CreativePurpose.Director); Assert.Equal(0, calls); // All-empty input never sends.
            Find<TextBox>(w, "DirectorRole").Text = "主持人"; // One field is enough to supply a requirement.
            var pending = w.GenerateCreativeAsync(CreativePurpose.Director); await entered.Task;
            Assert.Equal("AI 正在生成…", Find<Button>(w, "AiDirector").Content);
            switch (action)
            {
                case "provider": Find<ComboBox>(w, "Provider").SelectedItem = TtsProvider.Windows; break;
                case "mode": Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign; break;
                case "control": Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.NaturalLanguage; break;
                case "cancel": w.CancelAi(); break;
                case "close": w.Close(); break;
                case "new": await w.GenerateCreativeAsync(CreativePurpose.Director); break;
                default: Find<TextBox>(w, action).Text = "编辑草稿"; break;
            }
            var expected = new[] { "DirectorRole", "DirectorScene", "DirectorDirection" }.Select(n => Find<TextBox>(w, n).Text).ToArray();
            release.SetResult(); await pending; Assert.Equal(expected, new[] { "DirectorRole", "DirectorScene", "DirectorDirection" }.Select(n => Find<TextBox>(w, n).Text).ToArray());
            if (action != "close") Assert.Equal("AI 生成", Find<Button>(w, "AiDirector").Content);
        }
        finally { release.TrySetResult(); w.Close(); Directory.Delete(folder, true); }
    });
}
