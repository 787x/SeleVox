using System.Runtime.InteropServices;
using System.Windows.Input;
using SeleVox.App;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class OcrCursorTests
{
    [Fact] public Task EmbeddedCursorLoadsAndSafelyFallsBack() => OcrDesktopTests.RunSta(() =>
    {
        using var cursor = OcrSelectionCursor.Load();
        Assert.NotSame(Cursors.Cross, cursor);
        Assert.Same(Cursors.Cross, OcrSelectionCursor.Load(() => null));
        Assert.Same(Cursors.Cross, OcrSelectionCursor.Load(() => new MemoryStream([0, 1, 2])));
        Assert.Same(Cursors.Cross, OcrSelectionCursor.Load(() => throw new IOException("fixture")));
        return Task.CompletedTask;
    });

    [WindowsOcrFact] public Task LightDarkOverlayNativeCursorAndBothDragDirections() => OcrDesktopTests.RunSta(async () =>
    {
        GetPhysicalCursorPos(out var saved);
        var focus = Native.GetForegroundWindow();
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            foreach (var dark in new[] { false, true })
            {
                var bounds = screen.Bounds;
                using var snapshot = new ScreenSnapshot(bounds, new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb));
                var pixels = snapshot.Image;
                using (var graphics = System.Drawing.Graphics.FromImage(pixels))
                {
                    graphics.Clear(dark ? System.Drawing.Color.Black : System.Drawing.Color.White);
                    // Distinct pixels let the crop assertion detect even one-pixel drift.
                    for (var x = 100; x < 400; x++)
                        pixels.SetPixel(x, 150, System.Drawing.Color.FromArgb(x % 256, 80, 190));
                }
                foreach (var reverse in new[] { false, true })
                {
                    using var selection = new ScreenSelection([snapshot], CancellationToken.None);
                    var overlay = Assert.Single(selection.Overlays);
                    await Task.Delay(120);
                    var from = new System.Drawing.Point(bounds.X + (reverse ? 400 : 100), bounds.Y + (reverse ? 250 : 100));
                    var to = new System.Drawing.Point(bounds.X + (reverse ? 100 : 400), bounds.Y + (reverse ? 100 : 250));
                    Assert.True(SetCursorPos(from.X, from.Y)); await Task.Delay(80);
                    var current = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                    Assert.True(GetCursorInfo(ref current)); Assert.Equal(1, current.Flags);
                    Assert.Equal(from.X, current.Point.X); Assert.Equal(from.Y, current.Point.Y);
                    Assert.True(GetIconInfo(current.Cursor, out var info));
                    try
                    {
                        Assert.False(info.IsIcon);
                        Assert.NotEqual(0, GetObject(info.Color, Marshal.SizeOf<BitmapInfo>(), out var bitmap));
                        Assert.Equal(bitmap.Width / 2, (int)info.X); Assert.Equal(bitmap.Height / 2, (int)info.Y);
                        Assert.NotSame(Cursors.Cross, overlay.Cursor);
                        SavePreview(current.Cursor, bitmap.Width, bitmap.Height, dark);
                    }
                    finally { DeleteObject(info.Mask); DeleteObject(info.Color); }
                    mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
                    Assert.True(SetCursorPos(to.X, to.Y)); await Task.Delay(50); mouse_event(4, 0, 0, 0, 0);
                    using var crop = await selection.Result.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.Equal(300, crop.Width); Assert.Equal(150, crop.Height);
                    for (var x = 0; x < crop.Width; ++x) Assert.Equal(pixels.GetPixel(x + 100, 150), crop.GetPixel(x, 50));
                    Assert.Empty(selection.Overlays); Assert.Null(overlay.Cursor);
                }
            }
        }
        finally { SetCursorPos(saved.X, saved.Y); SetForegroundWindow(focus); }
    });

    private static void SavePreview(nint cursor, int width, int height, bool dark)
    {
        var directory = Environment.GetEnvironmentVariable("SELEVOX_UI_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        using var image = new System.Drawing.Bitmap(360, 120);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
        {
            graphics.Clear(dark ? System.Drawing.Color.Black : System.Drawing.Color.White);
            using var font = new System.Drawing.Font("Arial", 16);
            graphics.DrawString("OCR selection text 123", font, dark ? System.Drawing.Brushes.White : System.Drawing.Brushes.Black, 10, 40);
            var dc = graphics.GetHdc();
            try { Assert.True(DrawIconEx(dc, 115, 40, cursor, width, height, 0, 0, 3)); }
            finally { graphics.ReleaseHdc(dc); }
        }
        Directory.CreateDirectory(directory);
        image.Save(Path.Combine(directory, $"ocr-cursor-{(dark ? "dark" : "light")}-{width}.png"));
    }

    [Fact] public void AllEmbeddedFramesHaveOpaqueCenteredHotspotsAndBothOutlines()
    {
        using var stream = typeof(ScreenSelection).Assembly.GetManifestResourceStream("SeleVox.App.OcrSelection.cur");
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16()); Assert.Equal(2, reader.ReadUInt16()); Assert.Equal(4, reader.ReadUInt16());
        foreach (var size in new[] { 32, 48, 64, 96 })
        {
            Assert.Equal(size, reader.ReadByte()); Assert.Equal(size, reader.ReadByte()); reader.ReadUInt16();
            Assert.Equal(size / 2, reader.ReadUInt16()); Assert.Equal(size / 2, reader.ReadUInt16());
            var length = reader.ReadUInt32(); var offset = reader.ReadUInt32(); var nextEntry = stream.Position;
            Assert.True(offset + length <= stream.Length);
            stream.Position = offset;
            Assert.Equal(40, reader.ReadInt32()); Assert.Equal(size, reader.ReadInt32()); Assert.Equal(size * 2, reader.ReadInt32());
            stream.Position = offset + 40;
            var pixels = reader.ReadBytes(size * size * 4);
            var center = ((size - 1 - size / 2) * size + size / 2) * 4;
            Assert.Equal(255, pixels[center + 3]);
            Assert.Contains(Enumerable.Range(0, size * size), p => pixels[p * 4] == 16 && pixels[p * 4 + 3] == 255);
            Assert.Contains(Enumerable.Range(0, size * size), p => pixels[p * 4] == 255 && pixels[p * 4 + 1] == 255 && pixels[p * 4 + 2] == 255 && pixels[p * 4 + 3] == 255);
            stream.Position = nextEntry;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint X, Y; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public Point Point; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint cursor, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern int GetObject(nint handle, int size, out BitmapInfo bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
