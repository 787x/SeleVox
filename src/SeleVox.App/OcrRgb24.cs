using System.Drawing;
using System.Drawing.Imaging;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed record OcrRgb24(byte[] Pixels, int Width, int Height, int Stride)
{
    internal static unsafe OcrRgb24 FromBitmap(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 32 * 1024 * 1024)
            throw new UserFeedbackException("框选区域超过 Xiaomi OCR 图像上限，请缩小区域。");

        var format = bitmap.PixelFormat;
        var bytesPerPixel = format switch
        {
            PixelFormat.Format24bppRgb => 3,
            PixelFormat.Format32bppRgb or PixelFormat.Format32bppArgb => 4,
            _ => 0
        };
        var bounds = new Rectangle(0, 0, width, height);
        if (bytesPerPixel == 0)
        {
            using var converted = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(converted))
                graphics.DrawImageUnscaled(bitmap, 0, 0);
            return Read(converted, bounds, PixelFormat.Format32bppArgb, 4);
        }
        return Read(bitmap, bounds, format, bytesPerPixel);
    }

    private static unsafe OcrRgb24 Read(Bitmap bitmap, Rectangle bounds, PixelFormat format, int bytesPerPixel)
    {
        var stride = checked(bounds.Width * 3);
        var pixels = new byte[checked(stride * bounds.Height)];
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, format);
        try
        {
            if (data.Scan0 == 0 || Math.Abs((long)data.Stride) < bounds.Width * bytesPerPixel)
                throw new UserFeedbackException("无法读取屏幕 OCR 图像。");
            fixed (byte* target = pixels)
            {
                for (var y = 0; y < bounds.Height; y++)
                {
                    var source = (byte*)data.Scan0 + (long)y * data.Stride;
                    var output = target + y * stride;
                    for (var x = 0; x < bounds.Width; x++, source += bytesPerPixel, output += 3)
                    {
                        output[0] = source[2];
                        output[1] = source[1];
                        output[2] = source[0];
                    }
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return new(pixels, bounds.Width, bounds.Height, stride);
    }
}
