using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;
namespace SeleVox.Tests;

[Collection("WPF desktop")]
[Trait("Category", "Offline")]
public sealed class StartupSettingsWindowTests
{
    [Fact]
    public Task TraySynchronizationPreservesDraftAndSubsequentSave() => OcrDesktopTests.RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.MiMo);
        MainWindow? window = null;
        window = new(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            var startup = (CheckBox)window.FindName("AutoStart");
            var model = (TextBox)window.FindName("LlmModel");
            var streaming = (CheckBox)window.FindName("LlmStreaming");
            model.Text = "draft-model"; streaming.IsChecked = false;
            startup.IsChecked = true; // An unsaved startup edit loses to the explicit tray operation.
            window.SetStartupSettings(false);
            Assert.False(startup.IsChecked); Assert.Equal("draft-model", model.Text); Assert.False(streaming.IsChecked);
            saved = saved with { StartWithWindows = true }; window.SetStartupSettings(true);
            window.Show(); window.Hide(); window.Show();
            Assert.True(startup.IsChecked);
            for (var i = 0; i < 100 && ((ListBox)window.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
            AiWindowTests.Descendants<Button>(window).Single(b => b.Content as string == "保存设置").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(saved.StartWithWindows); Assert.Equal("draft-model", saved.LlmModel); Assert.False(saved.LlmStreaming);
            saved = saved with { StartWithWindows = false }; window.SetAutomaticSettings(saved);
            Assert.False(startup.IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void StartupOnlyComparisonHandlesRebuiltCollectionsAndDetectsBusinessChanges()
    {
        var saved = new Settings(Applications: [new(@"C:\app.exe")], OutputDeviceIds: ["default"], MiMoOverallStyles: ["style"]);
        var updated = saved with { StartWithWindows = true, Applications = [new(@"C:\app.exe")], OutputDeviceIds = ["default"], MiMoOverallStyles = ["style"], CustomPrompts = [] };
        Assert.True(SeleVox.App.App.OnlyStartupChanged(saved, updated));
        Assert.False(SeleVox.App.App.OnlyStartupChanged(saved, updated with { AutoAsk = true }));
        Assert.False(SeleVox.App.App.OnlyStartupChanged(saved, updated with { Rate = 1.5 }));
        Assert.False(SeleVox.App.App.OnlyStartupChanged(saved, updated with { Applications = [] }));
        Assert.False(SeleVox.App.App.OnlyStartupChanged(saved, updated with { AllowAutomaticLlm = true }));
    }
}

