namespace SeleVox.Core;

// Only drag or a nearby second click can request an automatic capture.
public sealed class MouseSelectionGesture(uint doubleClickMilliseconds, int doubleClickWidth, int doubleClickHeight,
    int dragWidth = 4, int dragHeight = 4)
{
    private (nint Window, int X, int Y, uint Time)? down, previousClick;
    private bool secondClick;
    public void Down(nint window, int x, int y, uint time)
    {
        secondClick = previousClick is { } previous && previous.Window == window &&
            unchecked(time - previous.Time) <= doubleClickMilliseconds &&
            Math.Abs((long)x - previous.X) <= doubleClickWidth / 2 && Math.Abs((long)y - previous.Y) <= doubleClickHeight / 2;
        down = (window, x, y, time);
    }
    public bool Up(nint window, int x, int y, uint time)
    {
        var start = down; down = null;
        if (start is not { } d || window == 0 || window != d.Window) { Reset(); return false; }
        var drag = Math.Abs((long)x - d.X) >= dragWidth || Math.Abs((long)y - d.Y) >= dragHeight;
        var result = drag || secondClick;
        previousClick = result ? null : d;
        return result;
    }
    public void Reset() { down = previousClick = null; secondClick = false; }
}
