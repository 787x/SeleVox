using Microsoft.Win32;
namespace SeleVox.App;
internal static class StartupRegistration
{
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (!enabled) { key.DeleteValue("SeleVox", false); return; }
        var exe = Environment.ProcessPath!;
        if (!exe.EndsWith("SeleVox.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请使用 SeleVox.App.exe 启动后启用开机启动。");
        key.SetValue("SeleVox", $"\"{exe}\" --background");
    }
}
