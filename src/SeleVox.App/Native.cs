using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SeleVox.App;
internal static class Native
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
}
internal sealed class Hotkeys : IDisposable
{
    private readonly HwndSource source = new(new HwndSourceParameters("SeleVox hotkeys") { ParentWindow = new nint(-3) });
    private readonly List<int> registered = [];
    public event Action<int, nint>? Pressed;
    public List<string> Conflicts { get; } = [];
    public Hotkeys()
    {
        source.AddHook(HandleMessage);
        Register(1, 0x52, "Ctrl+Alt+R"); Register(2, 0x50, "Ctrl+Alt+P"); Register(3, 0x58, "Ctrl+Alt+X"); Register(4, 0x4F, "Ctrl+Alt+O");
    }
    private void Register(int id, uint key, string name)
    {
        if (Native.RegisterHotKey(source.Handle, id, 0x0001 | 0x0002 | 0x4000, key)) registered.Add(id);
        else Conflicts.Add($"{name} 注册失败（错误 {Marshal.GetLastWin32Error()}），可能已被占用。");
    }
    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312) { handled = true; Pressed?.Invoke((int)wParam, Native.GetForegroundWindow()); }
        return 0;
    }
    public void Dispose()
    {
        foreach (var id in registered) Native.UnregisterHotKey(source.Handle, id);
        source.RemoveHook(HandleMessage); source.Dispose();
    }
}
