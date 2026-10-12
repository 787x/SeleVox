using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class EdgeVoiceWindowTests
{
    [Fact] public Task RepeatRefreshProviderSwitchAndCloseRejectStaleResults() => RunSta(async () =>
    {
        var requests = new List<(CancellationToken Token, TaskCompletionSource<IReadOnlyList<EdgeVoiceChoice>> Result)>();
        var saved = new Settings(Provider: TtsProvider.Edge, EdgeVoice: "zh-CN-XiaoxiaoNeural", AllowAutomaticCloud: true);
        var cancelCalls = 0;
        var window = new MainWindow(saved, _ => null, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => ++cancelCalls,
            token => { var result = new TaskCompletionSource<IReadOnlyList<EdgeVoiceChoice>>(); requests.Add((token, result)); return result.Task; });
        var provider = (ComboBox)window.FindName("Provider"); var voice = (ComboBox)window.FindName("EdgeVoice");
        var filter = (TextBox)window.FindName("EdgeLocale"); var notice = (TextBlock)window.FindName("Notice");
        var refresh = (Button)window.FindName("QueryEdgeVoices");
        void Query() => refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        EdgeVoiceChoice Choice(string id, string locale) => new(id, id + " (" + locale + ")", locale);
        Assert.Equal(saved.EdgeVoice, voice.SelectedValue); Assert.False(((CheckBox)window.FindName("AutomaticEdge")).IsChecked);
        Query(); Query(); Assert.True(requests[0].Token.IsCancellationRequested);
        requests[1].Result.SetResult([Choice("zh-CN-XiaoxiaoNeural", "zh-CN"), Choice("en-US-AriaNeural", "en-US")]); await Task.Yield();
        requests[0].Result.SetResult([Choice("stale", "zh-CN")]); await Task.Yield();
        Assert.DoesNotContain(voice.Items.Cast<EdgeVoiceChoice>(), v => v.Id == "stale");
        filter.Text = "en-US"; Assert.Contains(voice.Items.Cast<EdgeVoiceChoice>(), v => v.Id == "en-US-AriaNeural");
        Assert.Equal(saved.EdgeVoice, voice.SelectedValue); // filtering must retain configured choice
        Query(); provider.SelectedIndex = 0; Assert.True(requests[2].Token.IsCancellationRequested);
        var previousNotice = notice.Text; requests[2].Result.SetException(new UserFeedbackException("late error")); await Task.Yield();
        Assert.Equal(previousNotice, notice.Text); Assert.True(cancelCalls > 0);
        provider.SelectedIndex = 2; Query(); window.Close(); Assert.True(requests[3].Token.IsCancellationRequested);
        requests[3].Result.SetResult([Choice("late-closed", "zh-CN")]); await Task.Yield();
        Assert.DoesNotContain(voice.Items.Cast<EdgeVoiceChoice>(), v => v.Id == "late-closed");
    });

    [Fact] public Task QueryFailurePreservesVoiceAndSavingDoesNotAlterAzureConsent() => RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.Edge, EdgeVoice: "zh-CN-XiaoxiaoNeural", AllowAutomaticCloud: true);
        Settings? persisted = null;
        var window = new MainWindow(saved, updated => { persisted = updated; return null; }, () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { },
            _ => Task.FromException<IReadOnlyList<EdgeVoiceChoice>>(new UserFeedbackException("网络不可用")));
        ((Button)window.FindName("QueryEdgeVoices")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
        Assert.Equal(saved.EdgeVoice, ((ComboBox)window.FindName("EdgeVoice")).SelectedValue);
        Assert.Contains("保留", ((TextBlock)window.FindName("Notice")).Text);
        // Wait for actual device enumeration before invoking the production save handler.
        for (var i = 0; i < 100 && ((ListBox)window.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
        var save = FindButtons(window).Single(b => b.Content as string == "保存设置"); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(persisted); Assert.Equal(saved.EdgeVoice, persisted.EdgeVoice); Assert.True(persisted.AllowAutomaticCloud); Assert.False(persisted.AllowAutomaticEdge);
        window.Close();
    });
    private static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
    [Fact] public Task ProviderSelectionAndImmediateConsentDoNotPretendProviderWasSaved() => RunSta(async () =>
    {
        var saved = new Settings(); MainWindow? window = null;
        window = new MainWindow(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { },
            _ => Task.FromResult<IReadOnlyList<EdgeVoiceChoice>>([new("zh-CN-XiaoxiaoNeural", "晓晓 (zh-CN)", "zh-CN")]));
        var hint = (TextBlock)window.FindName("ProviderSaveHint");
        ((ComboBox)window.FindName("Provider")).SelectedIndex = 2;
        Assert.Contains("Windows 本地", hint.Text); Assert.Contains("尚未保存", hint.Text);
        var consent = (CheckBox)window.FindName("AutomaticEdge"); consent.IsChecked = true;
        consent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(saved.AllowAutomaticEdge); Assert.Equal(TtsProvider.Windows, saved.Provider);
        Assert.Contains("尚未保存", hint.Text);
        ((Button)window.FindName("QueryEdgeVoices")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ComboBox)window.FindName("EdgeVoice")).SelectedValue = "zh-CN-XiaoxiaoNeural";
        for (var i = 0; i < 100 && ((ListBox)window.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
        var saveButton = FindButtons(window).Single(b => b.Content as string == "保存设置");
        Assert.Equal(2, Grid.GetRow((UIElement)saveButton.Parent)); // fixed footer, outside ScrollViewer
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(TtsProvider.Edge, saved.Provider); Assert.Equal("zh-CN-XiaoxiaoNeural", saved.EdgeVoice);
        Assert.Contains("当前朗读使用：Edge", hint.Text); Assert.DoesNotContain("尚未保存", hint.Text);
        window.Close();
    });
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
