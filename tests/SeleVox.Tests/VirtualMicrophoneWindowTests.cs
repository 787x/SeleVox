using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;
[Collection("WPF desktop")]
public sealed class VirtualMicrophoneWindowTests
{
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    [VirtualMicFact] public Task RealSettingsSelectVerifySaveDisableAndPreserveIds() => OcrDesktopTests.RunSta(async () =>
    {
        var route = VirtualMicrophoneWindowsTests.ActualRoute();
        var settings = new Settings(); var stops = 0; MainWindow? window = null;
        window = new(settings, updated => { settings = updated; window!.SetAutomaticSettings(updated); return null; }, () => { }, () => stops++, () => { }, () => Task.FromResult<string?>(null), () => { });
        try
        {
            window.Show();
            var render = (ComboBox)window.FindName("VirtualRender"); var capture = (ComboBox)window.FindName("VirtualCapture");
            var enabled = (CheckBox)window.FindName("VirtualMicEnabled"); var test = (Button)window.FindName("VirtualTestButton");
            await Wait(() => render.Items.Count > 0 && capture.Items.Count > 0);
            Assert.False(enabled.IsChecked); render.SelectedValue = route.RenderId; capture.SelectedValue = route.CaptureId;
            test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => ((TextBlock)window.FindName("Notice")).Text.Contains("录音验证通过")); Assert.Equal(1, stops);
            enabled.IsChecked = true;
            window.GetType().GetMethod("Save_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
            Assert.True(settings.VirtualMicrophoneEnabled); Assert.Equal(route, settings.VirtualMicrophone);
            Assert.Contains("就绪", ((TextBlock)window.FindName("VirtualMicStatus")).Text);
            ((GroupBox)window.FindName("VirtualMicGroup")).BringIntoView(); await Task.Delay(100); window.UpdateLayout();
            var directory = Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, "pr17-virtual-microphone.png")); png.Save(output);
            }
            enabled.IsChecked = false;
            window.GetType().GetMethod("Save_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
            Assert.False(settings.VirtualMicrophoneEnabled); Assert.Equal(route, settings.VirtualMicrophone);
        }
        finally { window.Close(); }
    });
}
