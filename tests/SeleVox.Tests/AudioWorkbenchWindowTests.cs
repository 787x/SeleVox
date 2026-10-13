using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class AudioWorkbenchWindowTests
{
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
            ((TextBox)window.FindName("CustomStyle")).Text = "温柔";
            Click(window, "添加风格");
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
    private static void Click(Window window, string content) => Buttons(window).Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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
