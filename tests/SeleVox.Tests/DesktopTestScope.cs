using System.Runtime.InteropServices;
using System.Text;

namespace SeleVox.Tests;

// Desktop input, foreground and hotkeys are session-wide even across separate test hosts.
internal sealed class DesktopTestScope : IDisposable
{
    private readonly Mutex mutex = new(false, @"Local\SeleVox.Tests.Desktop");
    private bool owns;
    internal static DesktopTestScope Acquire()
    {
        var scope = new DesktopTestScope();
        try
        {
            try { scope.owns = scope.mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { scope.owns = true; }
            if (!scope.owns) throw new InvalidOperationException("其他 SeleVox 桌面测试正在占用本会话的鼠标、焦点和热键，请串行运行。");
            if (Environment.GetEnvironmentVariable("SELEVOX_OCR_DESKTOP_TESTS") == "1") CheckInteractiveDesktop();
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
    private static void CheckInteractiveDesktop()
    {
        if (!Environment.UserInteractive)
            throw new InvalidOperationException("桌面回归需要交互式用户会话。");
        var input = OpenInputDesktop(0, false, 1);
        if (input == 0)
            throw new InvalidOperationException("桌面回归需要已解锁的交互式用户桌面；当前无法访问输入桌面。");
        try
        {
            var currentName = Name(GetThreadDesktop(GetCurrentThreadId())); var inputName = Name(input);
            if (currentName != inputName)
                throw new InvalidOperationException($"测试桌面 {currentName} 与输入桌面 {inputName} 不同，请解锁并保持测试会话可见。");
        }
        finally { CloseDesktop(input); }
    }
    private static string Name(nint desktop)
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, text, text.Capacity * sizeof(char), out _))
            throw new InvalidOperationException("无法确认测试桌面身份，请在可见的交互式桌面运行。");
        return text.ToString();
    }
    public void Dispose() { if (owns) { owns = false; mutex.ReleaseMutex(); } mutex.Dispose(); }
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder text, int length, out int needed);
}
