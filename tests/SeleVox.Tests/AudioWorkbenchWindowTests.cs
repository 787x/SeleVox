using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class AudioWorkbenchWindowTests
{
    [Theory]
    [InlineData(TtsProvider.Windows)] [InlineData(TtsProvider.Azure)] [InlineData(TtsProvider.Edge)]
    [InlineData(TtsProvider.MiMo)] [InlineData(TtsProvider.OpenAiCompatible)]
    public Task DefaultsSaveOnlySelectedProviderAndRestoreOtherSavedParameters(TtsProvider selected) => OcrDesktopTests.RunSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var main = new Settings(Provider: TtsProvider.MiMo, Rate: .8, AutoSpeak: true, AutoAsk: true,
            AllowAutomaticCloud: true, AllowAutomaticEdge: true, AllowAutomaticMiMo: true, AllowAutomaticOpenAi: true,
            OpenAiBaseUrl: "http://localhost:8472/v1", OpenAiAuthorizedBaseUrl: "http://localhost:8472/v1",
            MiMoModel: "daily-mimo-model", MiMoVoice: "Mia", OpenAiModel: "daily-openai-model");
        var saved = WorkbenchPreferences.OnlyTts(new Settings(VoiceId: SpeechAudio.GetVoices().First().Id,
            AzureRegion: "eastasia", AzureVoice: "zh-CN-XiaoxiaoNeural", EdgeVoice: "zh-CN-XiaoyiNeural", EdgePitchHz: 25,
            MiMoModel: "saved-mimo-model", MiMoVoice: "Dean", MiMoStyle: "saved calm", MiMoOverallStyles: ["温柔"],
            OpenAiModel: "saved-openai-model", OpenAiVoice: "saved-voice", OpenAiInstructions: "saved instructions"));
        // PR11's original file serialized the entire Settings record; it must still load.
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new WorkbenchPreferences(saved, 100, 150)));
        var speech = new AudioGenerationTests.Speech();
        AudioWorkbenchWindow Open() => new(() => main, () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, path, speech);
        var window = Open();
        try
        {
            window.Show(); ((ComboBox)window.FindName("Provider")).SelectedItem = selected;
            ((TextBox)window.FindName("InputText")).Text = "private input fixture";
            ((Slider)window.FindName("Rate")).Value = 1.65;
            // Every inactive provider gets an incomplete draft; none may block saving.
            if (selected != TtsProvider.Windows) ((ComboBox)window.FindName("LocalVoice")).SelectedIndex = -1;
            ((TextBox)window.FindName("Region")).Text = selected == TtsProvider.Azure ? "westus" : "INVALID";
            if (selected != TtsProvider.Azure) ((ComboBox)window.FindName("AzureVoice")).SelectedIndex = -1;
            if (selected == TtsProvider.Edge) ((Slider)window.FindName("Pitch")).Value = 60;
            ((TextBox)window.FindName("MiMoModel")).Text = selected == TtsProvider.MiMo ? "updated-mimo-model" : "";
            ((TextBox)window.FindName("MiMoStyle")).Text = "unsaved other style";
            ((TextBox)window.FindName("OpenAiModel")).Text = selected == TtsProvider.OpenAiCompatible ? "updated-openai-model" : "";
            ((TextBox)window.FindName("Instructions")).Text = selected == TtsProvider.OpenAiCompatible ? "updated instructions" : new string('x', 4097);
            Click(window, "保存工作台默认参数");
            Assert.Contains("已保存", ((TextBlock)window.FindName("GenerationStatus")).Text);
            var persisted = WorkbenchPreferences.Load(path, new()).Tts;
            Assert.True(persisted.IsValid); Assert.Equal(selected, persisted.Provider); Assert.Equal(1.65, persisted.Rate);
            Assert.Equal(saved.VoiceId, persisted.VoiceId);
            Assert.Equal(selected == TtsProvider.Azure ? "westus" : saved.AzureRegion, persisted.AzureRegion);
            Assert.Equal(saved.AzureVoice, persisted.AzureVoice); Assert.Equal(saved.EdgeVoice, persisted.EdgeVoice);
            Assert.Equal(selected == TtsProvider.Edge ? 60 : saved.EdgePitchHz, persisted.EdgePitchHz);
            Assert.Equal(selected == TtsProvider.MiMo ? "updated-mimo-model" : saved.MiMoModel, persisted.MiMoModel);
            Assert.Equal(saved.MiMoVoice, persisted.MiMoVoice); Assert.Equal(saved.MiMoOverallStyles, persisted.MiMoOverallStyles);
            Assert.Equal(selected == TtsProvider.MiMo ? "unsaved other style" : saved.MiMoStyle, persisted.MiMoStyle);
            Assert.Equal(selected == TtsProvider.OpenAiCompatible ? "updated-openai-model" : saved.OpenAiModel, persisted.OpenAiModel);
            Assert.Equal(saved.OpenAiVoice, persisted.OpenAiVoice);
            Assert.Equal(selected == TtsProvider.OpenAiCompatible ? "updated instructions" : saved.OpenAiInstructions, persisted.OpenAiInstructions);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("private input fixture", json); Assert.DoesNotContain("Automatic", json);
            Assert.DoesNotContain("Authorized", json); Assert.DoesNotContain("BaseUrl", json); Assert.DoesNotContain("Key", json);
            Assert.Equal(TtsProvider.MiMo, main.Provider); Assert.Equal(.8, main.Rate); Assert.True(main.AutoSpeak); Assert.True(main.AllowsAutomaticProvider);
            Assert.Equal("http://localhost:8472/v1", main.OpenAiBaseUrl); Assert.Equal(main.OpenAiBaseUrl, main.OpenAiAuthorizedBaseUrl);

            if (selected == TtsProvider.Windows)
            {
                ((ComboBox)window.FindName("Provider")).SelectedItem = TtsProvider.OpenAiCompatible;
                Click(window, "保存工作台默认参数"); Assert.Contains("未保存", ((TextBlock)window.FindName("GenerationStatus")).Text);
                Assert.Equal(json, File.ReadAllText(path)); ((ComboBox)window.FindName("Provider")).SelectedItem = selected;
            }

            // The selected provider's own invalid configuration must still be refused.
            switch (selected)
            {
                case TtsProvider.Windows: ((ComboBox)window.FindName("LocalVoice")).SelectedIndex = -1; break;
                case TtsProvider.Azure: ((TextBox)window.FindName("Region")).Clear(); break;
                case TtsProvider.Edge: break; // Missing selection is covered with an unconfigured voice below.
                case TtsProvider.MiMo: ((TextBox)window.FindName("MiMoModel")).Clear(); break;
                default: ((TextBox)window.FindName("OpenAiModel")).Clear(); break;
            }
            if (selected != TtsProvider.Edge)
            {
                Click(window, "保存工作台默认参数"); Assert.Contains("未保存", ((TextBlock)window.FindName("GenerationStatus")).Text);
                Assert.Equal(json, File.ReadAllText(path));
            }
            window.Close(); window = Open(); window.Show();
            Assert.Equal(selected, window.Draft().Provider); Assert.Equal(persisted.MiMoModel, window.Draft().MiMoModel);
            Assert.Equal(persisted.OpenAiModel, window.Draft().OpenAiModel); Assert.Equal(persisted.OpenAiInstructions, window.Draft().OpenAiInstructions);
            Assert.Equal(persisted.AzureRegion, window.Draft().AzureRegion); Assert.Equal(persisted.EdgePitchHz, window.Draft().EdgePitchHz);
            Assert.Empty(speech.Texts);
        }
        finally { window.Close(); File.Delete(path); }
        return Task.CompletedTask;
    });
    [Theory] [InlineData(TtsProvider.Azure)] [InlineData(TtsProvider.Edge)] [InlineData(TtsProvider.OpenAiCompatible)]
    public Task DefaultsRejectMissingOnlineVoiceAndUnsupportedInstructions(TtsProvider provider) => OcrDesktopTests.RunSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var saved = new Settings(AzureRegion: "eastasia", OpenAiModel: "tts-1", OpenAiInstructions: "unsupported");
        new WorkbenchPreferences(saved).Save(path); var before = File.ReadAllText(path);
        var window = new AudioWorkbenchWindow(() => new(), () => { }, (_, _) => Task.CompletedTask, () => { }, () => { }, path);
        try
        {
            window.Show(); ((ComboBox)window.FindName("Provider")).SelectedItem = provider;
            Click(window, "保存工作台默认参数");
            Assert.Contains("未保存", ((TextBlock)window.FindName("GenerationStatus")).Text); Assert.Equal(before, File.ReadAllText(path));
        }
        finally { window.Close(); File.Delete(path); }
        return Task.CompletedTask;
    });
    [Fact] public Task FixedSampleActualTextCachedPreviewSnapshotProviderDraftsAndIndependentClose() => OcrDesktopTests.RunSta(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var main = new Settings(AutoSpeak: true, AllowAutomaticMiMo: true);
        var speech = new AudioGenerationTests.Speech(); var played = new List<GeneratedAudio>(); var settingsOpened = false;
        var window = new AudioWorkbenchWindow(() => main, () => settingsOpened = true, (audio, _) => { played.Add(audio); return Task.CompletedTask; }, () => { }, () => { }, path, speech);
        try
        {
            window.Show();
            var provider = (ComboBox)window.FindName("Provider");
            for (var i = 0; i < 5; ++i) { provider.SelectedItem = (TtsProvider)i; Assert.Equal((TtsProvider)i, window.Draft().Provider); Render(window, ((TtsProvider)i).ToString()); }
            provider.SelectedItem = TtsProvider.MiMo;
            ((ComboBox)window.FindName("MiMoVoice")).SelectedItem = "Dean";
            ((TextBox)window.FindName("MiMoStyle")).Text = "calm";
            ((TextBox)window.FindName("MiMoCustomStyle")).Text = "温柔";
            Click(window, "添加");
            var groups = (StackPanel)window.FindName("MiMoPresetGroups");
            ToggleButton Preset(string name) => groups.Children.OfType<WrapPanel>().SelectMany(r => r.Children.OfType<ToggleButton>()).Single(b => Equals(b.Content, name));
            void Toggle(string name) => Preset(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Toggle("清亮"); Assert.Equal(new[] { "温柔", "清亮" }, window.Draft().MiMoOverallStyles);
            Toggle("唱歌"); Assert.Equal(new[] { "唱歌" }, window.Draft().MiMoOverallStyles);
            Toggle("温柔"); Assert.Equal(new[] { "温柔" }, window.Draft().MiMoOverallStyles);
            var search = (TextBox)window.FindName("MiMoStyleSearch"); search.Text = "方言";
            Assert.Equal(4, groups.Children.OfType<WrapPanel>().Single().Children.Count); search.Text = "";
            ((TextBox)window.FindName("MiMoCustomStyle")).Text = "(畸形)"; Click(window, "添加");
            Assert.NotEmpty(((TextBlock)window.FindName("MiMoStyleError")).Text); Assert.Equal(new[] { "温柔" }, window.Draft().MiMoOverallStyles);
            Click(window, "清空整体风格"); Assert.Empty(window.Draft().MiMoOverallStyles!);
            Toggle("温柔");
            var expander = LogicalDescendants(window).OfType<Expander>().Single(e => Equals(e.Header, "浏览 / 搜索预设与添加自定义风格")); expander.IsExpanded = true;
            window.UpdateLayout(); Render(window, "MiMo-expanded");
            await window.GenerateAsync(true); Assert.Equal(AudioWorkbenchWindow.SampleText, speech.Texts.Single()); Assert.Single(played);
            ((TextBox)window.FindName("InputText")).Text = "实际输入🙂"; await window.GenerateAsync(false); Assert.Equal("实际输入🙂", speech.Texts.Last()); Assert.Single(played);
            var summary = ((TextBox)window.FindName("ResultSummary")).Text;
            provider.SelectedItem = TtsProvider.Edge; await window.PreviewAsync(); await window.PreviewAsync();
            Assert.Equal(2, speech.Texts.Count); Assert.Equal(3, played.Count); Assert.Same(played[1], played[2]);
            Assert.Equal(TtsProvider.MiMo, played[2].Configuration.Provider); Assert.Equal("Dean", played[2].Configuration.MiMoVoice); Assert.Equal(summary, ((TextBox)window.FindName("ResultSummary")).Text);
            Assert.Equal(TtsProvider.Windows, main.Provider); Assert.True(main.AutoSpeak); Assert.True(main.AllowAutomaticMiMo);
            Click(window, "前往主设置 / 管理凭据和输出设备"); Assert.True(settingsOpened);
            provider.SelectedItem = TtsProvider.MiMo; Click(window, "保存工作台默认参数");
            Assert.Equal("Dean", WorkbenchPreferences.Load(path, main).Tts.MiMoVoice);
            var settings = new MainWindow(main, _ => null, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { }); settings.Show(); settings.Close(); Assert.True(window.IsVisible);
        }
        finally { window.Close(); File.Delete(path); }
    });
    [Fact] public Task LateCancelledFailedAndClosedGenerationCannotReplaceSuccessfulResult() => OcrDesktopTests.RunSta(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"); var count = 0;
        var pending = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new AudioGenerationTests.Speech((_, _, _) => ++count == 1 ? Task.FromResult<Stream>(new MemoryStream(AudioGenerationTests.Wave(), false)) : pending.Task);
        var played = 0;
        var window = new AudioWorkbenchWindow(() => new(), () => { }, (_, _) => { ++played; return Task.CompletedTask; }, () => { }, () => { }, path, speech);
        try
        {
            window.Show(); ((TextBox)window.FindName("InputText")).Text = "fixture";
            await window.GenerateAsync(false); var summary = ((TextBox)window.FindName("ResultSummary")).Text;
            var stale = window.GenerateAsync(false); await AudioGenerationTests.Wait(() => count == 2); window.CancelGeneration();
            pending.SetResult(new MemoryStream(AudioGenerationTests.Wave(), false)); await stale;
            Assert.Equal(summary, ((TextBox)window.FindName("ResultSummary")).Text); await window.PreviewAsync(); Assert.Equal(1, played);
            ((TextBox)window.FindName("InputText")).Clear(); await window.GenerateAsync(false); Assert.Equal(summary, ((TextBox)window.FindName("ResultSummary")).Text);
            window.Close(); await window.PreviewAsync(); Assert.Equal(1, played);
        }
        finally { window.Close(); File.Delete(path); }
    });
    [Fact] public Task ReplacementAndCloseDiscardNonCooperativeLateProvider() => OcrDesktopTests.RunSta(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var pending = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0; var played = 0;
        var speech = new AudioGenerationTests.Speech((_, _, _) => Interlocked.Increment(ref count) == 1 ? pending.Task : Task.FromResult<Stream>(new MemoryStream(AudioGenerationTests.Wave(), false)));
        var window = new AudioWorkbenchWindow(() => new(), () => { }, (_, _) => { ++played; return Task.CompletedTask; }, () => { }, () => { }, path, speech);
        try
        {
            window.Show(); ((TextBox)window.FindName("InputText")).Text = "old";
            var old = window.GenerateAsync(true); await AudioGenerationTests.Wait(() => count == 1);
            ((TextBox)window.FindName("InputText")).Text = "new"; var replacement = window.GenerateAsync(false);
            pending.SetResult(new MemoryStream(AudioGenerationTests.Wave(), false)); await old; await replacement;
            Assert.Equal([AudioWorkbenchWindow.SampleText, "new"], speech.Texts); Assert.Equal(0, played);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var closingSpeech = new AudioGenerationTests.Speech((_, _, _) => pending.Task);
            window.Close();
            window = new AudioWorkbenchWindow(() => new(), () => { }, (_, _) => { ++played; return Task.CompletedTask; }, () => { }, () => { }, path, closingSpeech);
            window.Show(); var closing = window.GenerateAsync(true); await AudioGenerationTests.Wait(() => closingSpeech.Texts.Count == 1);
            window.Close(); pending.SetResult(new MemoryStream(AudioGenerationTests.Wave(), false)); await closing;
            Assert.Equal(0, played); Assert.Equal("尚未生成", ((TextBox)window.FindName("ResultSummary")).Text);
        }
        finally { window.Close(); File.Delete(path); }
    });
    internal static void Click(Window window, string content) => Buttons(window).Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    { foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) { yield return child; foreach (var nested in LogicalDescendants(child)) yield return nested; } }
    private static void Render(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var file = File.Create(Path.Combine(directory, "pr11-" + name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<Button> Buttons(DependencyObject parent)
    { foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) { if (child is Button button) yield return button; foreach (var nested in Buttons(child)) yield return nested; } }
}
