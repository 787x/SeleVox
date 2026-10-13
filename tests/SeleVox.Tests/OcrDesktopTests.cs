using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;
using Drawing = System.Drawing;

namespace SeleVox.Tests;

public sealed class WindowsOcrFactAttribute : FactAttribute
{
    public WindowsOcrFactAttribute() { if (Environment.GetEnvironmentVariable("SELEVOX_OCR_DESKTOP_TESTS") != "1") Skip = "Enable explicitly on an interactive Windows desktop."; }
}

[Trait("Category", "WindowsOcr")]
[Collection("WPF desktop")]
public sealed class OcrDesktopTests(ITestOutputHelper output)
{
    [WindowsOcrFact] public Task RepeatSnapshotExcludesVisibleOldResultAndCancelRestoresEditedText() => RunSta(async () =>
    {
        var originalClipboard = ClipboardBackup.Capture(); var originalFocus = Native.GetForegroundWindow();
        var reads = 0; var automaticSelections = 0;
        using var monitor = new AutomaticMouseMonitor(); monitor.Selected += (_, _, _) => ++automaticSelections;
        using var source = new Drawing.Bitmap(1100, 700, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var text = OcrTests.Fixture("Fresh screen capture\nSecond line text", "Arial"))
        using (var graphics = Drawing.Graphics.FromImage(source))
        { graphics.Clear(Drawing.Color.FromArgb(26, 92, 132)); graphics.DrawImageUnscaled(text, 0, 0); }
        var fixture = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Content = new Image { Source = SelectionOverlay.ToSource(source), Stretch = Stretch.Fill } };
        var result = new OcrResultWindow(new("OLD OCR RESULT", TextSource.ScreenOcr), () => new Settings(), _ => { ++reads; return Task.CompletedTask; }) { Topmost = true };
        try
        {
            Clipboard.SetText("repeat OCR clipboard baseline");
            fixture.Show(); var fixtureHandle = new WindowInteropHelper(fixture).Handle;
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            Assert.True(SetWindowPos(fixtureHandle, new nint(-1), screen.X + 60, screen.Y + 60, 1100, 700, 0x0040));
            result.ShowResult(new("OLD OCR RESULT", TextSource.ScreenOcr));
            var resultHandle = new WindowInteropHelper(result).Handle;
            Assert.True(SetWindowPos(resultHandle, new nint(-1), screen.X + 180, screen.Y + 120, 600, 420, 0x0040));
            var editor = Descendants<TextBox>(result).Single(); editor.Text = "edited OLD OCR RESULT"; editor.Select(3, 4);
            await Task.Delay(100); GetWindowRect(resultHandle, out var rect);
            var sample = new Drawing.Point((rect.Left + rect.Right) / 2, rect.Bottom - 100);
            var expected = source.GetPixel(sample.X - screen.X - 60, sample.Y - screen.Y - 60).ToArgb();
            var visible = await Task.Run(() => ScreenSnapshot.Capture(CancellationToken.None));
            try
            {
                Assert.True(result.IsVisible);
                Assert.NotEqual(expected, visible.Single(s => s.Bounds == screen).Image.GetPixel(sample.X - screen.X, sample.Y - screen.Y).ToArgb());
            }
            finally { foreach (var shot in visible) shot.Dispose(); }
            var left = result.Left; var top = result.Top; var width = result.Width; var height = result.Height;
            using var operation = new TextCaptureOperation(); var cancelled = 0;
            result.HideForCapture();
            try
            {
                await operation.RunAsync(async token =>
                {
                    // No delay after Hide: production DwmFlush must remove the old result from the snapshot.
                    var snapshots = await ScreenSnapshot.CaptureAsync(token);
                    try
                    {
                        Assert.False(result.IsVisible);
                        var snapshot = snapshots.Single(s => s.Bounds == screen);
                        var actual = snapshot.Image.GetPixel(sample.X - screen.X, sample.Y - screen.Y).ToArgb();
                        output.WriteLine($"Hidden-result sample: expected {expected:X8}, actual {actual:X8}");
                        Assert.Equal(expected, actual);
                        using var crop = snapshot.Image.Clone(new Drawing.Rectangle(60, 60, 1100, 240), System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        var fresh = await new LocalOcr().RecognizeAsync(crop, OcrLanguage.English, token);
                        Assert.Contains("Fresh screen capture", fresh.Text); Assert.Contains("Secondlinetext", fresh.Text.Replace(" ", ""));
                        Assert.DoesNotContain("OLD OCR RESULT", fresh.Text);
                        using var selection = new ScreenSelection(snapshots, token); await Task.Delay(100);
                        selection.Cancel();
                        using var image = await selection.Result.WaitAsync(TimeSpan.FromSeconds(3));
                        return await new LocalOcr().RecognizeAsync(image, OcrLanguage.English, token);
                    }
                    finally { foreach (var snapshot in snapshots) snapshot.Dispose(); }
                }, result.ShowResult, error => throw new Exception(error), () => ++cancelled);
            }
            finally { if (!operation.IsActive) result.RestoreAfterCapture(); }
            Assert.Equal(1, cancelled); Assert.True(result.IsVisible); Assert.Equal(resultHandle, new WindowInteropHelper(result).Handle);
            Assert.Equal("edited OLD OCR RESULT", editor.Text); Assert.Equal(3, editor.SelectionStart); Assert.Equal(4, editor.SelectionLength);
            Assert.Equal(left, result.Left); Assert.Equal(top, result.Top); Assert.Equal(width, result.Width); Assert.Equal(height, result.Height);
            Assert.Equal(0, reads); Assert.Equal(0, automaticSelections); Assert.Equal("repeat OCR clipboard baseline", Clipboard.GetText());
        }
        finally
        {
            result.Close(); fixture.Close(); SetForegroundWindow(originalFocus);
            if (originalClipboard is not null) Clipboard.SetDataObject(originalClipboard, true); else Clipboard.Clear();
        }
    });
    [WindowsOcrFact] public Task ReplacementAndFailureRetainHiddenResultUntilLatestFinishes() => RunSta(async () =>
    {
        var result = new OcrResultWindow(new("prior edited text", TextSource.ScreenOcr), () => new Settings(), _ => throw new Exception("Unexpected read"));
        var foreground = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
        using var operation = new TextCaptureOperation(); var failures = 0;
        async Task Request(Task<CapturedText> compute)
        {
            result.HideForCapture();
            try { await operation.RunAsync(_ => compute, result.ShowResult, _ => ++failures); }
            finally { if (!operation.IsActive) result.RestoreAfterCapture(); }
        }
        try
        {
            result.ShowResult(new("prior edited text", TextSource.ScreenOcr)); await Task.Delay(60);
            result.Left = 80; result.Top = 90;
            var first = new TaskCompletionSource<CapturedText>(); var second = new TaskCompletionSource<CapturedText>();
            var old = Request(first.Task); var current = Request(second.Task);
            first.SetResult(new("obsolete result", TextSource.ScreenOcr)); await old;
            Assert.False(result.IsVisible); Assert.Equal("prior edited text", Descendants<TextBox>(result).Single().Text);
            foreground.Show(); foreground.Activate(); await Task.Delay(60); var active = Native.GetForegroundWindow();
            second.SetException(new UserFeedbackException("fixed OCR failure")); await current;
            Assert.Equal(1, failures); Assert.True(result.IsVisible); Assert.Equal(active, Native.GetForegroundWindow());
            Assert.Equal("prior edited text", Descendants<TextBox>(result).Single().Text); Assert.Equal(80, result.Left); Assert.Equal(90, result.Top);
            result.WindowState = WindowState.Minimized; result.HideForCapture(); result.RestoreAfterCapture();
            Assert.Equal(WindowState.Minimized, result.WindowState); Assert.Equal("prior edited text", Descendants<TextBox>(result).Single().Text);
        }
        finally { result.Close(); foreground.Close(); }
    });
    [WindowsOcrFact] public Task RepeatedResultKeepsWindowPositionSizeAndRestoresFromMinimized() => RunSta(async () =>
    {
        GetPhysicalCursorPos(out var cursor);
        var original = ClipboardBackup.Capture(); var reads = 0;
        var window = new OcrResultWindow(new("first fixture", TextSource.ScreenOcr), () => new Settings(), _ => { ++reads; return Task.CompletedTask; });
        try
        {
            Clipboard.SetText("PR6 unchanged clipboard");
            window.ShowResult(new("first fixture", TextSource.ScreenOcr)); await Task.Delay(80);
            window.Left = 80; window.Top = 90; window.Width = 500; window.Height = 380;
            await Task.Delay(80);
            var handle = new WindowInteropHelper(window).Handle;
            // Give this test process real foreground input, as the OCR overlay does in the app.
            GetWindowRect(handle, out var bounds); SetCursorPos(bounds.Left + 100, bounds.Top + 15);
            mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0); await Task.Delay(80);
            window.HideForCapture(); window.ShowResult(new("second fixture", TextSource.ScreenOcr)); await Task.Delay(80);
            Assert.Equal(handle, new WindowInteropHelper(window).Handle); Assert.Equal(handle, Native.GetForegroundWindow());
            Assert.Equal(80, window.Left); Assert.Equal(90, window.Top); Assert.Equal(500, window.Width); Assert.Equal(380, window.Height);
            Assert.Equal("second fixture", Descendants<TextBox>(window).Single().Text);
            window.WindowState = WindowState.Minimized; await Task.Delay(80);
            window.HideForCapture(); window.ShowResult(new("third fixture", TextSource.ScreenOcr)); await Task.Delay(80);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(80, window.Left); Assert.Equal(90, window.Top); Assert.Equal(500, window.Width); Assert.Equal(380, window.Height);
            Assert.Equal(handle, Native.GetForegroundWindow()); Assert.Equal("third fixture", Descendants<TextBox>(window).Single().Text);
            Assert.Equal(0, reads); Assert.Equal("PR6 unchanged clipboard", Clipboard.GetText());
        }
        finally { window.Close(); SetCursorPos(cursor.X, cursor.Y); if (original is not null) Clipboard.SetDataObject(original, true); else Clipboard.Clear(); }
    });
    [WindowsOcrFact] public Task RealDesktopCaptureDragEscapeAndCancelReleaseAllOverlays() => RunSta(async () =>
    {
        GetPhysicalCursorPos(out var savedCursor);
        var originalFocus = Native.GetForegroundWindow();
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var bounds = screen.Bounds;
                using var fixture = OcrTests.Fixture("屏幕文字识别测试\nEnglish screen capture 123", "Microsoft YaHei");
                var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                    Content = new Image { Source = SelectionOverlay.ToSource(fixture), Stretch = Stretch.Fill } };
                try
                {
                    window.Show();
                    var handle = new WindowInteropHelper(window).Handle;
                    var width = Math.Min(1100, bounds.Width - 120); var height = Math.Min(240, bounds.Height - 120);
                    Assert.True(SetWindowPos(handle, new nint(-1), bounds.X + 60, bounds.Y + 60, width, height, 0x0040)); window.Activate();
                    await Task.Delay(180);
                    output.WriteLine($"Display {bounds}, DPI {GetDpiForWindow(handle)}");
                    var snapshots = await Task.Run(() => ScreenSnapshot.Capture(CancellationToken.None));
                    try
                    {
                        using var selection = new ScreenSelection(snapshots, CancellationToken.None);
                        await Task.Delay(150);
                        foreach (var overlay in selection.Overlays)
                        {
                            var hwnd = new WindowInteropHelper(overlay).Handle; GetWindowRect(hwnd, out var physical);
                            var expected = snapshots[selection.Overlays.ToList().IndexOf(overlay)].Bounds;
                            Assert.Equal(expected, Drawing.Rectangle.FromLTRB(physical.Left, physical.Top, physical.Right, physical.Bottom));
                        }
                        // Actual desktop mouse messages, reverse drag; crop uses physical pixels.
                        var from = new Drawing.Point(bounds.X + 60 + width - 8, bounds.Y + 60 + height - 8);
                        var to = new Drawing.Point(bounds.X + 68, bounds.Y + 68);
                        SetCursorPos(from.X, from.Y); mouse_event(0x0002, 0, 0, 0, 0); await Task.Delay(60);
                        SetCursorPos(to.X, to.Y); await Task.Delay(60); mouse_event(0x0004, 0, 0, 0, 0);
                        using var image = await selection.Result.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.Empty(selection.Overlays);
                        var source = snapshots.Single(s => s.Bounds == bounds);
                        var crop = ScreenGeometry.Crop(bounds, from, to);
                        for (var x = 0; x < image.Width; x += 37)
                            Assert.Equal(source.Image.GetPixel(crop.X + x, crop.Y + 15), image.GetPixel(x, 15));
                        var text = await new LocalOcr().RecognizeAsync(image, OcrLanguage.ChineseEnglish, CancellationToken.None);
                        Assert.Contains("屏幕文字识别测试", text.Text.Replace(" ", "")); Assert.Contains("English", text.Text);
                        using var escaped = new ScreenSelection(snapshots, CancellationToken.None); await Task.Delay(100);
                        keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0);
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => escaped.Result.WaitAsync(TimeSpan.FromSeconds(3))); Assert.Empty(escaped.Overlays);
                        using var cancelled = new CancellationTokenSource(); using var stopped = new ScreenSelection(snapshots, cancelled.Token);
                        await Task.Run(cancelled.Cancel); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.Result); Assert.Empty(stopped.Overlays);
                        using var switched = new ScreenSelection(snapshots, CancellationToken.None); await Task.Delay(100);
                        Assert.True(SetForegroundWindow(handle));
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switched.Result.WaitAsync(TimeSpan.FromSeconds(3))); Assert.Empty(switched.Overlays);
                        for (var attempt = 0; attempt < 3; ++attempt)
                        {
                            using var retry = new ScreenSelection(snapshots, CancellationToken.None); retry.Cancel();
                            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.Result); Assert.Empty(retry.Overlays);
                        }
                    }
                    finally { foreach (var snapshot in snapshots) snapshot.Dispose(); }
                }
                finally { window.Close(); }
            }
        }
        finally { SetCursorPos(savedCursor.X, savedCursor.Y); SetForegroundWindow(originalFocus); }
    });

    [WindowsOcrFact] public Task ResultEditsClipboardOnlyOnClickAndShowsSavedService() => RunSta(async () =>
    {
        var original = ClipboardBackup.Capture();
        OcrResultWindow? window = null;
        try
        {
            Clipboard.SetText("PR6 clipboard baseline");
            var settings = new Settings(Provider: TtsProvider.MiMo); var reads = new List<CapturedText>();
            window = new OcrResultWindow(new("屏幕文字识别测试\nEnglish screen capture 123\n\n在此编辑文字，主动复制或朗读。", TextSource.ScreenOcr), () => settings, text => { reads.Add(text); return Task.CompletedTask; });
            window.Show(); await Task.Delay(80);
            RenderIfRequested(window, "ocr-result");
            Assert.Equal("PR6 clipboard baseline", Clipboard.GetText());
            var editor = Descendants<TextBox>(window).Single(); editor.Text = "edited OCR fixture";
            Assert.Contains("小米 MiMo", Descendants<TextBlock>(window).Single(t => t.Text.Contains("语速")).Text);
            Click(window, "朗读文字"); Assert.Equal("edited OCR fixture", Assert.Single(reads).Text); Assert.Equal("PR6 clipboard baseline", Clipboard.GetText());
            Click(window, "复制文字"); Assert.Equal("edited OCR fixture", Clipboard.GetText());
            editor.Text = " \n "; Click(window, "复制文字"); Click(window, "朗读文字"); Assert.Single(reads); Assert.Equal("edited OCR fixture", Clipboard.GetText());
            settings = settings with { Provider = TtsProvider.OpenAiCompatible, OpenAiBaseUrl = "https://example.org/v1" };
            window.RefreshProviderNotice(); Assert.Contains("https://example.org/v1", Descendants<TextBlock>(window).Single(t => t.Text.Contains("语速")).Text);
            Click(window, "关闭"); Assert.Equal("edited OCR fixture", Clipboard.GetText()); Assert.Equal("", editor.Text);
        }
        finally { window?.Close(); if (original is not null) Clipboard.SetDataObject(original, true); else Clipboard.Clear(); }
    });

    [WindowsOcrFact] public Task ActualOcrHotkeyCanBeReceivedAndUnregistered() => RunSta(async () =>
    {
        using (var hotkeys = new Hotkeys())
        {
            Assert.DoesNotContain(hotkeys.Conflicts, c => c.StartsWith("Ctrl+Alt+O"));
            var received = new TaskCompletionSource<int>(); hotkeys.Pressed += (id, _) => { if (id == 4) received.TrySetResult(id); };
            keybd_event(0x11, 0, 0, 0); keybd_event(0x12, 0, 0, 0); keybd_event(0x4F, 0, 0, 0);
            keybd_event(0x4F, 0, 2, 0); keybd_event(0x12, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
            Assert.Equal(4, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        using var restarted = new Hotkeys(); Assert.DoesNotContain(restarted.Conflicts, c => c.StartsWith("Ctrl+Alt+O"));
    });

    private static void Click(Window window, string label) => Descendants<Button>(window).Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal static void RenderIfRequested(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        window.Show(); window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory); using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    internal static Task RunSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var oldDpi = SetThreadDpiAwarenessContext(new nint(-4));
            try
            {
                using var desktop = DesktopTestScope.Acquire();
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(async () =>
                {
                    try { await action(); } catch (Exception ex) { failure = ex; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                });
                Dispatcher.Run();
            }
            catch (Exception ex) { failure ??= ex; }
            finally { if (oldDpi != 0) SetThreadDpiAwarenessContext(oldDpi); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return Task.Run(() =>
        {
            // Native/OLE thread teardown must finish before another desktop test starts.
            if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("桌面测试 STA 线程未在既有 45 秒预算内退出。");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        });
    }
    [StructLayout(LayoutKind.Sequential)] private struct PhysicalPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PhysicalRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out PhysicalPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out PhysicalRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint dpi);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
