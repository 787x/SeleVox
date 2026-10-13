using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SeleVox.Core;
using Tesseract;

namespace SeleVox.App;

internal static class ScreenGeometry
{
    internal const long MaxScreenPixels = 32_000_000, MaxCropPixels = 8_000_000;
    internal static System.Windows.Point OverlayPoint(Rectangle monitor, Point point, double dipWidth, double dipHeight)
        => new((point.X - monitor.X) * dipWidth / monitor.Width, (point.Y - monitor.Y) * dipHeight / monitor.Height);
    // All inputs/outputs here are physical desktop pixels, never WPF DIP.
    internal static Rectangle Crop(Rectangle monitor, Point start, Point end)
    {
        if (!monitor.Contains(start) || end.X < monitor.Left || end.X > monitor.Right || end.Y < monitor.Top || end.Y > monitor.Bottom)
            throw new UserFeedbackException("单次框选须在同一台显示器内，请重新框选。");
        var area = Rectangle.Intersect(monitor, Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));
        if (area.Width < 8 || area.Height < 8) throw new UserFeedbackException("框选区域太小，请重新框选。");
        if ((long)area.Width * area.Height > MaxCropPixels) throw new UserFeedbackException("框选区域过大，请缩小到 800 万像素以内。");
        area.Offset(-monitor.X, -monitor.Y); return area;
    }
}

internal sealed class ScreenSnapshot(Rectangle bounds, Bitmap image) : IDisposable
{
    internal Rectangle Bounds { get; } = bounds;
    internal Bitmap Image { get; } = image;
    internal static async Task<List<ScreenSnapshot>> CaptureAsync(CancellationToken token)
    {
        // Let the UI dispatcher finish visibility/layout/render work after hiding a result.
        // Capture then waits for DWM presentation on the worker before reading desktop pixels.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => Capture(token), token);
    }
    internal static List<ScreenSnapshot> Capture(CancellationToken token)
    {
        // Thread DPI context also covers test hosts without the application manifest.
        var old = SetThreadDpiAwarenessContext(new nint(-4));
        var snapshots = new List<ScreenSnapshot>();
        try
        {
            // Wait for hidden OCR results and closed overlays to leave the composed desktop.
            if (DwmFlush() < 0) throw new UserFeedbackException("桌面合成截图暂不可用，请重试。");
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Sum(s => (long)s.Bounds.Width * s.Bounds.Height) > ScreenGeometry.MaxScreenPixels)
                throw new UserFeedbackException("桌面截图超过 3200 万像素上限，请降低显示分辨率后重试。");
            foreach (var screen in screens)
            {
                token.ThrowIfCancellationRequested();
                var bounds = screen.Bounds;
                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                try
                {
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                    snapshots.Add(new(bounds, bitmap));
                }
                catch { bitmap.Dispose(); throw new UserFeedbackException("无法捕获屏幕，请检查桌面访问权限；受保护的画面不支持 OCR。"); }
            }
            token.ThrowIfCancellationRequested(); return snapshots;
        }
        catch { foreach (var snapshot in snapshots) snapshot.Dispose(); throw; }
        finally { if (old != 0) SetThreadDpiAwarenessContext(old); }
    }
    public void Dispose() => Image.Dispose();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}

internal sealed class LocalOcr(string? dataDirectory = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<CapturedText> RecognizeAsync(Bitmap image, OcrLanguage language, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var data = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "OcrData");
                var models = language == OcrLanguage.English ? new[] { "eng" } : new[] { "chi_sim", "eng" };
                if (models.Any(m => !File.Exists(Path.Combine(data, m + ".traineddata"))))
                    throw new UserFeedbackException("缺少 OCR 语言模型，请恢复程序目录中的 OcrData 文件夹。");
                try
                {
                    // Native recognition is serialized and cannot immediately abort. Never publish a cancelled result.
                    using var engine = new TesseractEngine(data, string.Join("+", models), EngineMode.LstmOnly);
                    engine.SetVariable("debug_file", "NUL");
                    using var bytes = new MemoryStream(); image.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
                    using var pix = Pix.LoadFromMemory(bytes.ToArray());
                    using var page = engine.Process(pix, PageSegMode.Auto);
                    var text = page.GetText().Replace("\r\n", "\n").Trim();
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(text)) throw new UserFeedbackException("未识别到文字。请框选清晰的中英文文字；空白或受保护的画面无法识别。");
                    return new CapturedText(text, TextSource.ScreenOcr);
                }
                catch (Exception ex) when (ex is not UserFeedbackException and not OperationCanceledException)
                { throw new UserFeedbackException("本地 OCR 引擎不可用。请检查 x64 原生 DLL、语言模型和 Visual C++ 2015–2022 x64 运行库。"); }
            }, token);
        }
        finally { gate.Release(); }
    }
}
