using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class HttpSpeechWindowTests
{
    [Fact] public Task StreamingOptionRestoresSavesAndExplainsUnsupportedModel() => RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.MiMo, MiMoStreaming: false); MainWindow? window = null;
        window = new MainWindow(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        var option = (CheckBox)window.FindName("MiMoStreaming"); var model = (TextBox)window.FindName("MiMoModel");
        Assert.False(option.IsChecked); Assert.True(option.IsEnabled);
        model.Text = "future-model"; Assert.False(option.IsEnabled); Assert.Contains("非流式", ((TextBlock)window.FindName("MiMoStreamingHint")).Text);
        model.Text = "mimo-v2.5-tts"; Assert.True(option.IsEnabled); option.IsChecked = true;
        await WaitForOutputs(window); Buttons(window).Single(b => b.Content as string == "保存设置").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(saved.MiMoStreaming); Assert.True(saved.UsesMiMoStreaming); RenderIfRequested(window, "mimo-streaming"); window.Close();
    });
    [Fact] public Task NewProvidersRestoreAndConsentDoesNotSaveDraftProvider() => RunSta(async () =>
    {
        var saved = new Settings(MiMoVoice: "Dean", MiMoStyle: "calm", MiMoModel: "fixture-mimo-model", OpenAiModel: "fixture-model", OpenAiVoice: "fixture-voice", OpenAiInstructions: "gentle");
        MainWindow? window = null; var cancelled = 0;
        window = new MainWindow(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => ++cancelled);
        Assert.Equal("Dean", ((ComboBox)window.FindName("MiMoVoice")).SelectedItem);
        Assert.Equal("calm", ((TextBox)window.FindName("MiMoStyle")).Text);
        Assert.Equal("fixture-mimo-model", ((TextBox)window.FindName("MiMoModel")).Text);
        var link = (System.Windows.Documents.Hyperlink)window.FindName("MiMoApiKeyLink");
        Assert.Equal("https://platform.xiaomimimo.com/console/api-keys", link.NavigateUri.AbsoluteUri);
        Assert.Equal(Dock.Right, DockPanel.GetDock((TextBlock)link.Parent));
        ((TextBox)window.FindName("MiMoModel")).Text = "next-compatible-model";
        Assert.Equal("fixture-model", ((TextBox)window.FindName("OpenAiModel")).Text);
        Assert.Equal("fixture-voice", ((TextBox)window.FindName("OpenAiVoice")).Text);
        Assert.Equal("gentle", ((TextBox)window.FindName("OpenAiInstructions")).Text);
        var provider = (ComboBox)window.FindName("Provider"); Assert.Equal(5, provider.Items.Count); provider.SelectedIndex = 3;
        Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("MiMoPanel")).Visibility);
        var consent = (CheckBox)window.FindName("AutomaticMiMo"); consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(saved.AllowAutomaticMiMo); Assert.False(saved.AllowAutomaticOpenAi); Assert.Equal(TtsProvider.Windows, saved.Provider);
        Assert.Contains("尚未保存", ((TextBlock)window.FindName("ProviderSaveHint")).Text);
        await WaitForOutputs(window);
        var save = Buttons(window).Single(b => b.Content as string == "保存设置"); save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(TtsProvider.MiMo, saved.Provider); Assert.Equal("Dean", saved.MiMoVoice); Assert.Equal("calm", saved.MiMoStyle);
        Assert.Equal("next-compatible-model", saved.MiMoModel);
        Assert.Contains("当前朗读使用：MiMo", ((TextBlock)window.FindName("ProviderSaveHint")).Text);
        RenderIfRequested(window, "mimo");
        provider.SelectedIndex = 4; Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("OpenAiPanel")).Visibility);
        RenderIfRequested(window, "openai");
        ((TextBox)window.FindName("OpenAiBaseUrl")).Text = "invalid-draft";
        ((TextBox)window.FindName("MiMoModel")).Text = "";
        provider.SelectedIndex = 0; save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(TtsProvider.Windows, saved.Provider);
        Assert.Equal("next-compatible-model", saved.MiMoModel); Assert.Equal("https://api.openai.com/v1", saved.OpenAiBaseUrl);
        Assert.True(cancelled > 0); window.Close();
    });
    [Fact] public Task AddressEditingRevokesConsentAndRequiresSaveBeforeReauthorization() => RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.OpenAiCompatible, AllowAutomaticOpenAi: true, OpenAiAuthorizedBaseUrl: "https://api.openai.com/v1");
        MainWindow? window = null; var cancelled = 0;
        window = new MainWindow(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => ++cancelled);
        var address = (TextBox)window.FindName("OpenAiBaseUrl"); var consent = (CheckBox)window.FindName("AutomaticOpenAi");
        Assert.True(consent.IsChecked); ((PasswordBox)window.FindName("OpenAiKey")).Password = "unsaved-fixture";
        address.Text = "http://localhost:8472/v1";
        Assert.False(saved.AllowAutomaticOpenAi); Assert.Null(saved.OpenAiAuthorizedBaseUrl); Assert.Equal("https://api.openai.com/v1", saved.OpenAiBaseUrl);
        Assert.False(consent.IsChecked); Assert.Equal("", ((PasswordBox)window.FindName("OpenAiKey")).Password); Assert.True(cancelled > 0);
        consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.False(saved.AllowAutomaticOpenAi); Assert.False(consent.IsChecked);
        await WaitForOutputs(window); Buttons(window).Single(b => b.Content as string == "保存设置").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("http://localhost:8472/v1", saved.OpenAiBaseUrl); Assert.False(saved.AllowsAutomaticProvider);
        consent.IsChecked = true; consent.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.True(saved.AllowsAutomaticProvider);
        Assert.Equal("http://localhost:8472/v1", saved.OpenAiAuthorizedBaseUrl);
        address.Text = "https://api.openai.com/v1"; Assert.False(saved.AllowAutomaticOpenAi);
        window.Close();
    });
    private static async Task WaitForOutputs(MainWindow window)
    {
        for (var i = 0; i < 100 && ((ListBox)window.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
    }
    private static void RenderIfRequested(MainWindow window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        window.Show(); window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in Buttons(child)) yield return nested;
        }
    }
    private static Task RunSta(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completed.SetResult(); }
                catch (Exception error) { completed.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completed.Task;
    }
}
