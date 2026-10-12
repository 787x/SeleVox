using System.Windows;

namespace SeleVox.App;

internal static class SelectionGeometry
{
    internal static bool Intersects(Rect bounds, Point start, Point end)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
        bounds.Inflate(8, 4);
        var dx = end.X - start.X; var dy = end.Y - start.Y;
        double first = 0, last = 1;
        // Clip the actual segment, not its bounding box (which overmatches diagonal drags).
        return Clip(-dx, start.X - bounds.Left, ref first, ref last) &&
            Clip(dx, bounds.Right - start.X, ref first, ref last) &&
            Clip(-dy, start.Y - bounds.Top, ref first, ref last) &&
            Clip(dy, bounds.Bottom - start.Y, ref first, ref last);
    }
    private static bool Clip(double direction, double distance, ref double first, ref double last)
    {
        if (direction == 0) return distance >= 0;
        var crossing = distance / direction;
        if (direction < 0) first = Math.Max(first, crossing);
        else last = Math.Min(last, crossing);
        return first <= last;
    }
}
