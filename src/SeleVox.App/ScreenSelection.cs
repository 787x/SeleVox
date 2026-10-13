using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeleVox.Core;
using Drawing = System.Drawing;

namespace SeleVox.App;

internal sealed class ScreenSelection : IDisposable
{
    private readonly List<SelectionOverlay> windows = [];
    private readonly System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly TaskCompletionSource<Drawing.Bitmap> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellation;
    private bool finished;
    internal ScreenSelection(IReadOnlyList<ScreenSnapshot> snapshots, CancellationToken token)
    {
        try
        {
            foreach (var snapshot in snapshots) windows.Add(new SelectionOverlay(snapshot, Finish, Cancel, CheckFocus, Fail));
            cancellation = token.Register(() =>
            {
                // Registration may also run on a worker. Always close on the overlay dispatcher.
                if (dispatcher.CheckAccess()) Cancel(); else dispatcher.BeginInvoke(Cancel);
            });
            token.ThrowIfCancellationRequested();
            foreach (var window in windows) window.Show();
            windows.Last().Activate();
        }
        catch { Dispose(); throw; }
    }
    internal Task<Drawing.Bitmap> Result => completion.Task;
    internal IReadOnlyList<SelectionOverlay> Overlays => windows;
    private void CheckFocus()
    {
        if (finished || windows.Count == 0) return;
        windows[0].Dispatcher.BeginInvoke(() =>
        {
            if (!finished && windows.All(w => new WindowInteropHelper(w).Handle != Native.GetForegroundWindow())) Cancel();
        });
    }
    private void Finish(ScreenSnapshot snapshot, Drawing.Point start, Drawing.Point end)
    {
        if (finished) return;
        try
        {
            var crop = ScreenGeometry.Crop(snapshot.Bounds, start, end);
            var image = snapshot.Image.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            finished = true; CloseWindows();
            if (!completion.TrySetResult(image)) image.Dispose();
        }
        catch (Exception ex) { finished = true; CloseWindows(); completion.TrySetException(ex); }
    }
    internal void Cancel()
    {
        if (finished) return;
        finished = true; CloseWindows(); completion.TrySetCanceled();
    }
    private void Fail(Exception error) { if (finished) return; finished = true; CloseWindows(); completion.TrySetException(error); }
    private void CloseWindows()
    {
        foreach (var window in windows) { window.ReleaseMouseCapture(); window.Close(); window.Content = null; }
        windows.Clear();
    }
    public void Dispose() { Cancel(); cancellation.Dispose(); }
}

internal sealed class SelectionOverlay : Window
{
    private readonly ScreenSnapshot snapshot;
    private readonly Action<ScreenSnapshot, Drawing.Point, Drawing.Point> selected;
    private readonly Action cancel;
    private readonly Action<Exception> fail;
    private readonly Canvas canvas = new();
    private readonly System.Windows.Shapes.Rectangle rectangle = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(30, 0, 150, 255)) };
    private Drawing.Point? start;
    internal SelectionOverlay(ScreenSnapshot snapshot, Action<ScreenSnapshot, Drawing.Point, Drawing.Point> selected, Action cancel, Action focusChanged, Action<Exception> fail)
    {
        this.snapshot = snapshot; this.selected = selected; this.cancel = cancel; this.fail = fail;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Cursor = Cursors.Cross; Background = Brushes.Black;
        var grid = new Grid();
        grid.Children.Add(new Image { Source = ToSource(snapshot.Image), Stretch = Stretch.Fill });
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)) });
        canvas.Children.Add(rectangle); grid.Children.Add(canvas);
        grid.Children.Add(new TextBlock { Text = "拖动框选 · 单次限一台显示器 · Esc 取消", Foreground = Brushes.White, Background = Brushes.Black,
            FontSize = 18, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) });
        Content = grid;
        SourceInitialized += (_, _) =>
        {
            var b = snapshot.Bounds;
            if (!SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), b.X, b.Y, b.Width, b.Height, 0x0040))
                throw new UserFeedbackException("无法放置屏幕框选窗口，请重试。");
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; cancel(); } };
        MouseLeftButtonDown += (_, e) => Input(() => { Activate(); start = CursorPosition(); if (!CaptureMouse()) throw new UserFeedbackException("无法捕获框选鼠标，请重试。"); UpdateRectangle(); e.Handled = true; });
        MouseMove += (_, _) => Input(() => { if (start is not null) UpdateRectangle(); });
        MouseLeftButtonUp += (_, e) => Input(() => { if (start is { } from) { var end = CursorPosition(); start = null; ReleaseMouseCapture(); selected(snapshot, from, end); e.Handled = true; } });
        Deactivated += (_, _) => focusChanged();
        Closing += (_, _) => { start = null; ReleaseMouseCapture(); };
        Closed += (_, _) => cancel();
    }
    private void Input(Action action) { try { action(); } catch (Exception ex) { fail(ex); } }
    private void UpdateRectangle()
    {
        if (start is not { } from) return;
        var to = CursorPosition(); var b = snapshot.Bounds;
        // Map physical pixels to this overlay's DIP surface, independently of other monitors' DPI.
        var a = ScreenGeometry.OverlayPoint(b, from, canvas.ActualWidth, canvas.ActualHeight);
        var z = ScreenGeometry.OverlayPoint(b, to, canvas.ActualWidth, canvas.ActualHeight);
        Canvas.SetLeft(rectangle, Math.Min(a.X, z.X)); Canvas.SetTop(rectangle, Math.Min(a.Y, z.Y));
        rectangle.Width = Math.Abs(z.X - a.X); rectangle.Height = Math.Abs(z.Y - a.Y);
    }
    internal static BitmapSource ToSource(Drawing.Bitmap image)
    {
        var bits = image.LockBits(new Drawing.Rectangle(0, 0, image.Width, image.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, bits.Scan0, bits.Stride * image.Height, bits.Stride);
            source.Freeze(); return source;
        }
        finally { image.UnlockBits(bits); }
    }
    private static Drawing.Point CursorPosition()
    {
        if (!GetPhysicalCursorPos(out var point)) throw new UserFeedbackException("无法读取屏幕鼠标位置，请重试。");
        return new(point.X, point.Y);
    }
    [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out PixelPoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
