using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class OcrResultWindow : Window
{
    private readonly TextBox editor;
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
    private readonly Func<Settings> settings;
    private Func<CapturedText, Task>? read;
    private bool hiddenForCapture;
    private Rect captureBounds;
    private WindowState captureState;
    internal OcrResultWindow(CapturedText result, Func<Settings> settings, Func<CapturedText, Task> read)
    {
        this.settings = settings; this.read = read;
        Title = "SeleVox · 屏幕 OCR 结果"; Width = 660; Height = 480; MinWidth = 400; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "本地 OCR · 可编辑原文，复制与朗读均须主动点击", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(heading);
        editor = new TextBox { Text = result.Text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16, MaxLength = 32768 };
        Grid.SetRow(editor, 1); grid.Children.Add(editor);
        var footer = new StackPanel(); Grid.SetRow(footer, 2); grid.Children.Add(footer); footer.Children.Add(notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; footer.Children.Add(actions);
        AddButton(actions, "复制文字", () =>
        {
            if (!HasText()) return;
            try { Clipboard.SetText(editor.Text); RefreshProviderNotice("已复制当前文字。 "); }
            catch (Exception) { notice.Text = "剪贴板正被占用或不可用，请重试。"; }
        });
        AddButton(actions, "朗读文字", () => { if (HasText() && this.read is { } action) { RefreshProviderNotice(); _ = action(new(editor.Text, TextSource.ScreenOcr)); } });
        AddButton(actions, "关闭", Close);
        Content = grid;
        Activated += (_, _) => RefreshProviderNotice();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { hiddenForCapture = false; editor.Clear(); this.read = null; Content = null; };
        RefreshProviderNotice();
    }
    private bool HasText()
    {
        if (!string.IsNullOrWhiteSpace(editor.Text)) return true;
        notice.Text = "没有文字，请先输入或重新框选。"; return false;
    }
    internal void ShowResult(CapturedText result)
    {
        if (hiddenForCapture) RestorePlacement(captureState == WindowState.Minimized ? WindowState.Normal : captureState);
        editor.Text = result.Text;
        editor.CaretIndex = 0; editor.ScrollToHome();
        RefreshProviderNotice();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); editor.Focus();
    }
    internal void HideForCapture()
    {
        // A replacement OCR inherits the hidden window; never clear its edited result.
        if (hiddenForCapture || !IsVisible) return;
        captureState = WindowState;
        captureBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Hide(); hiddenForCapture = true;
    }
    internal void RestoreAfterCapture()
    {
        if (!hiddenForCapture) return;
        RestorePlacement(captureState);
        // Cancellation/failure should restore visibility without stealing foreground focus
        // or changing a previously minimized window's state.
        var activated = ShowActivated;
        try { ShowActivated = false; Show(); }
        finally { ShowActivated = activated; }
    }
    private void RestorePlacement(WindowState state)
    {
        // Hide while minimized can cause WPF to adopt the minimized HWND's dimensions.
        // Restore the normal bounds explicitly before showing the same HWND again.
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = captureBounds.Left; Top = captureBounds.Top;
        Width = captureBounds.Width; Height = captureBounds.Height;
        WindowState = state; hiddenForCapture = false;
    }
    internal void RefreshProviderNotice(string prefix = "")
    {
        var current = settings();
        var destination = current.Provider switch
        {
            TtsProvider.Azure => "Azure", TtsProvider.Edge => "微软 Edge 在线服务", TtsProvider.MiMo => "小米 MiMo",
            TtsProvider.OpenAiCompatible => current.OpenAiBaseUrl, _ => null
        };
        notice.Text = prefix + (destination is null ? "朗读使用已保存的 Windows 本地 TTS。" : $"点击朗读将把当前文字发送到已保存服务：{destination}，可能产生费用。") + " 语速、音量与输出设备使用已保存设置。";
    }
    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 10, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
}
