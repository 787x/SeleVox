using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class OcrWindowTests
{
    [Fact] public Task LanguageRestoresAndSavesWithoutChangingProvider() => OcrDesktopTests.RunSta(async () =>
    {
        var saved = new Settings(Provider: TtsProvider.MiMo, OcrLanguage: OcrLanguage.English); MainWindow? window = null;
        window = new MainWindow(saved, updated => { saved = updated; window!.SetAutomaticSettings(updated); return null; },
            () => { }, () => { }, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            var choice = (ComboBox)window.FindName("OcrLanguageChoice"); Assert.Equal(1, choice.SelectedIndex); choice.SelectedIndex = 0;
            for (var i = 0; i < 100 && ((ListBox)window.FindName("Outputs")).Items.Count == 0; ++i) await Task.Delay(10);
            Buttons(window).Single(b => b.Content as string == "保存设置").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(OcrLanguage.ChineseEnglish, saved.OcrLanguage); Assert.Equal(TtsProvider.MiMo, saved.Provider);
            OcrDesktopTests.RenderIfRequested(window, "ocr-settings");
        }
        finally { window.Close(); }
    });
    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { if (child is Button button) yield return button; foreach (var nested in Buttons(child)) yield return nested; }
    }
}
