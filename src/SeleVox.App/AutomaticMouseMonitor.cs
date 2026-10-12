using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class AutomaticMouseMonitor : IDisposable
{
    private readonly MouseProcedure mouseCallback;
    private readonly EventProcedure eventCallback;
    private readonly MouseSelectionGesture gesture = new(GetDoubleClickTime(), GetSystemMetrics(36), GetSystemMetrics(37),
        Math.Max(4, GetSystemMetrics(68)), Math.Max(4, GetSystemMetrics(69)));
    private nint mouseHook, foregroundHook, focusHook;
    internal event Action? Invalidated;
    internal event Action<nint, System.Windows.Point>? Selected;
    internal AutomaticMouseMonitor()
    {
        mouseCallback = Mouse;
        eventCallback = (_, ev, _, _, _, _, _) =>
        {
            if (ev == 3) gesture.Reset();
            Invalidated?.Invoke();
        };
        try
        {
            mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
            foregroundHook = SetWinEventHook(3, 3, 0, eventCallback, 0, 0, 0);
            focusHook = SetWinEventHook(0x8005, 0x8005, 0, eventCallback, 0, 0, 0);
            if (mouseHook == 0 || foregroundHook == 0 || focusHook == 0) throw new Win32Exception();
        }
        catch { Dispose(); throw; }
    }
    private nint Mouse(int code, nint message, nint data)
    {
        // No UIA, process lookup, synthesis or dispatcher execution inside this callback.
        try
        {
            if (code >= 0 && (message == 0x0201 || message == 0x0202 || message == 0x0204 || message == 0x020A))
            {
                var input = Marshal.PtrToStructure<MouseInput>(data);
                var foreground = Native.GetForegroundWindow();
                if (message == 0x0201)
                {
                    Invalidated?.Invoke();
                    gesture.Down(foreground, input.Point.X, input.Point.Y, input.Time);
                }
                else if (message == 0x0202)
                {
                    if (gesture.Up(foreground, input.Point.X, input.Point.Y, input.Time))
                        Selected?.Invoke(foreground, new(input.Point.X, input.Point.Y));
                }
                else { gesture.Reset(); Invalidated?.Invoke(); }
            }
        }
        catch { gesture.Reset(); /* Never let a managed callback failure swallow application input. */ }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    public void Dispose()
    {
        if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
        if (foregroundHook != 0) UnhookWinEvent(foregroundHook);
        if (focusHook != 0) UnhookWinEvent(focusHook);
        mouseHook = foregroundHook = focusHook = 0;
        GC.KeepAlive(mouseCallback); GC.KeepAlive(eventCallback);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { internal Point Point; internal uint MouseData, Flags, Time; internal nuint ExtraInfo; }
    private delegate nint MouseProcedure(int code, nint message, nint data);
    private delegate void EventProcedure(nint hook, uint ev, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, MouseProcedure callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, EventProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}

internal static class ApplicationIdentity
{
    internal static string? FromWindow(nint window)
    {
        if (window == 0) return null;
        Native.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return null;
        return FromProcess(processId);
    }
    internal static string? FromProcess(uint processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) return null;
        var buffer = new StringBuilder(32768); var length = buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return null;
        var path = buffer.ToString();
        return Path.IsPathFullyQualified(path) ? path : null;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}
