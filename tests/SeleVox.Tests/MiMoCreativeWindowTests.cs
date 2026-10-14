using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class MiMoCreativeWindowTests
{
    private static T Find<T>(AudioWorkbenchWindow w, string name) => (T)w.FindName(name);
    [Fact] public Task DirectorRequestIsSnapshotAndCancelledLateDesignNeverBecomesCloneSource() => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "selevox-pr12-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Settings? snapshot = null;
        var speech = new AudioGenerationTests.Speech(async (_, s, _) => { snapshot = s; entered.TrySetResult(); await release.Task; return new MemoryStream(AudioGenerationTests.Wave(frames: 144000), false); });
        var w = new AudioWorkbenchWindow(() => new(Provider: TtsProvider.MiMo), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, Path.Combine(folder, "workbench.json"), speech);
        try
        {
            w.Show(); Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign; Find<TextBox>(w, "VoiceDescription").Text = "温暖";
            Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Director; Find<TextBox>(w, "DirectorRole").Text = "原始角色"; Find<TextBox>(w, "InputText").Text = "测试正文";
            var pending = w.GenerateAsync(false); await entered.Task; Find<TextBox>(w, "DirectorRole").Text = "新角色";
            w.CancelGeneration(); release.TrySetResult(); await pending;
            Assert.Equal("角色：原始角色", snapshot!.MiMoStyle); Assert.False(Find<Button>(w, "SaveCloneSource").IsEnabled); Assert.Equal("尚未生成", Find<TextBox>(w, "ResultSummary").Text);
            await w.GenerateAsync(false); Assert.True(Find<Button>(w, "SaveCloneSource").IsEnabled);
            Find<TextBox>(w, "VoiceDescription").Text = ""; await w.GenerateAsync(false);
            Assert.Contains("音色描述", Find<TextBlock>(w, "GenerationStatus").Text); Assert.Equal(2, speech.Texts.Count);
            Assert.True(Find<Button>(w, "SaveCloneSource").IsEnabled); // Last successful result remains reusable after a failed draft.
            w.Close(); Assert.Empty(new CloneLibrary(Path.Combine(folder, "clone-samples")).Load());
        }
        finally { release.TrySetResult(); w.Close(); Directory.Delete(folder, true); }
    });
    [Fact] public Task DesignToCloneReusesExactResultAndCachesPlaybackAndSave() => OcrDesktopTests.RunSta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "selevox-pr12-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var calls = 0; var plays = 0; GeneratedAudio? played = null;
        using var http = new HttpClient(new MiMoCreativeTests.Handler(async (request, token) => {
            ++calls; using var j = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (calls == 2) { var reference = j.RootElement.GetProperty("audio").GetProperty("voice").GetString()!;
                Assert.Equal(AudioGenerationTests.Wave(frames: 144000), Convert.FromBase64String(reference.Split(',')[1])); }
            return MiMoCreativeTests.Response(AudioGenerationTests.Wave(frames: 144000)); }));
        var speech = new AudioGenerationTests.Speech((text, s, token) => new MiMoSpeech(http, () => "fixture").SynthesizeAsync(text, s, token));
        var w = new AudioWorkbenchWindow(() => new(Provider: TtsProvider.MiMo), () => { }, (audio, _) => { played = audio; ++plays; return Task.CompletedTask; }, () => { }, () => { }, Path.Combine(folder, "workbench.json"), speech);
        try
        {
            w.Show(); Assert.False(Find<Button>(w, "SaveCloneSource").IsEnabled);
            await Assert.ThrowsAsync<UserFeedbackException>(() => w.SaveDesignSourceAsync("empty"));
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign;
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(w, "PresetPanel").Visibility);
            Find<TextBox>(w, "VoiceDescription").Text = "温柔、沉稳"; Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Director;
            Find<TextBox>(w, "DirectorRole").Text = "电台主持人"; Find<TextBox>(w, "DirectorScene").Text = "晚安"; Find<TextBox>(w, "DirectorDirection").Text = "放慢语速";
            Find<TextBox>(w, "InputText").Text = "固定六秒设计音色测试。";
            Assert.Contains("角色：电台主持人", Find<TextBox>(w, "RequestPreview").Text);
            await w.GenerateAsync(false); Assert.True(Find<Button>(w, "SaveCloneSource").IsEnabled); Assert.Equal(1, calls);
            await w.PreviewAsync(); await played!.SaveAsync(Path.Combine(folder, "output.wav"), false, default);
            Find<TextBox>(w, "DirectorRole").Text = "修改后的角色"; Assert.Contains("输入已修改", Find<TextBlock>(w, "GenerationStatus").Text);
            Assert.Contains("角色：电台主持人", Find<TextBox>(w, "ResultSummary").Text);
            await w.SaveDesignSourceAsync("设计样本"); Assert.Equal(1, calls); Assert.Single(new CloneLibrary(Path.Combine(folder, "clone-samples")).Load());
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceClone;
            Assert.Equal(2, Find<ComboBox>(w, "CreativeControl").Items.Count); Assert.NotNull(Find<ComboBox>(w, "CloneSamples").SelectedItem);
            await w.GenerateAsync(false); Assert.Equal(2, calls); Assert.False(Find<Button>(w, "SaveCloneSource").IsEnabled);
            await w.PreviewAsync(); await w.PreviewAsync(); await played!.SaveAsync(Path.Combine(folder, "clone-output.wav"), false, default); Assert.Equal(2, calls); Assert.Equal(3, plays);
            Assert.Equal(MiMoCreative.Model(MiMoCreativeMode.VoiceClone), played.Configuration.MiMoModel);
            Render(w, "clone");
        }
        finally { w.Close(); Directory.Delete(folder, true); }
    });
    [Fact] public Task ScriptInsertionModeSwitchAndProviderIsolation() => OcrDesktopTests.RunSta(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "selevox-pr12-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var w = new AudioWorkbenchWindow(() => new(Provider: TtsProvider.MiMo), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, Path.Combine(folder, "workbench.json"), new AudioGenerationTests.Speech());
        try
        {
            w.Show(); Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.AudioTags;
            var input = Find<TextBox>(w, "InputText"); input.Text = "你好世界。"; input.Select(2, 2);
            foreach (var tag in MiMoCreative.AudioTags)
            {
                var button = Find<WrapPanel>(w, "AudioTagButtons").Children.OfType<Button>().Single(b => (string)b.Tag == tag);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, input.SelectionLength);
            }
            Assert.Contains("[深呼吸]", input.Text); Assert.EndsWith("世界。", input.Text); Assert.Contains(input.Text, Find<TextBox>(w, "RequestPreview").Text);
            input.Text = string.Concat(Enumerable.Repeat("自然句边界分段。", 20)); Find<TextBox>(w, "SegmentCharacters").Text = "50";
            Assert.True(MiMoSpeech.SplitCreativeText(input.Text, w.Draft()).Count > 1); Render(w, "script");
            Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Singing; Assert.Equal(["唱歌"], w.Draft().MiMoOverallStyles!);
            Find<TextBox>(w, "MiMoCustomStyle").Text = "温柔";
            AudioWorkbenchWindowTests.Click(w, "添加"); Assert.Equal(MiMoControl.StyleTags, Find<ComboBox>(w, "CreativeControl").SelectedValue); Assert.Equal(["温柔"], w.Draft().MiMoOverallStyles!);
            Find<ComboBox>(w, "CreativeMode").SelectedValue = MiMoCreativeMode.VoiceDesign; Assert.Empty(w.Draft().MiMoOverallStyles!);
            Find<ComboBox>(w, "CreativeControl").SelectedValue = MiMoControl.Director; Find<TextBox>(w, "VoiceDescription").Text = "温暖";
            Find<TextBox>(w, "DirectorRole").Text = "主持人"; input.Text = "你好"; Render(w, "director-design");
            Find<ComboBox>(w, "Provider").SelectedItem = TtsProvider.Windows;
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(w, "MiMoPanel").Visibility); Assert.Equal(Visibility.Collapsed, Find<StackPanel>(w, "AudioTagPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>(w, "CloneReusePanel").Visibility); Render(w, "windows");
            Find<ComboBox>(w, "Provider").SelectedItem = TtsProvider.MiMo; w.Width = 900; w.Height = 620; w.UpdateLayout();
            foreach (var name in new[] { "Preview", "SaveAudio", "SaveCloneSource" })
            { var button = Find<Button>(w, name); var top = button.TransformToAncestor(w).Transform(new Point()); Assert.True(top.Y >= 0 && top.Y + button.ActualHeight <= w.ActualHeight); }
            Render(w, "minimum-size");
        }
        finally { w.Close(); Directory.Delete(folder, true); }
        return Task.CompletedTask;
    });
    private static void Render(AudioWorkbenchWindow w, string name)
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR") is not { Length: > 0 } directory) return;
        w.UpdateLayout(); Directory.CreateDirectory(directory); var target = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); target.Render(w);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target)); using var file = File.Create(Path.Combine(directory, "pr12-" + name + ".png")); encoder.Save(file);
    }
}
