using Microsoft.Win32;
namespace SeleVox.App;
internal sealed class StartupRegistrationException(string message) : InvalidOperationException(message);
internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            executable.IndexOfAny(['"', '\r', '\n', '\0', '<', '>', '|', '?', '*']) >= 0 ||
            executable[Path.GetPathRoot(executable)!.Length..].Contains(':') ||
            !Path.GetFileName(executable).Equals("SeleVox.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new StartupRegistrationException("无法注册开机启动：请使用合法的 SeleVox.App.exe 绝对路径。");
        var command = $"\"{executable}\" --background";
        // Windows Run commands have a documented 260-character limit.
        if (command.Length > 260) throw new StartupRegistrationException("开机启动命令超过 Windows 的 260 字符限制。");
        return command;
    }
    internal static bool IsSeleVoxCommand(string? command) => command is not null &&
        command.StartsWith('"') && command.EndsWith("\" --background", StringComparison.Ordinal) &&
        ValidCommand(command);
    private static bool ValidCommand(string command)
    {
        try { return Command(command[1..^14]) == command; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        Apply(key, enabled, Environment.ProcessPath);
    }
    internal static void ApplyAndSave(bool enabled, Action save)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        ApplyAndSave(key, enabled, Environment.ProcessPath, save);
    }
    internal static void ApplyAndSave(RegistryKey key, bool enabled, string? executable, Action save)
    {
        var previous = key.GetValue("SeleVox", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = previous is null ? RegistryValueKind.String : key.GetValueKind("SeleVox");
        Apply(key, enabled, executable);
        var changed = enabled ? !string.Equals(previous as string, Command(executable), StringComparison.Ordinal) :
            previous is string old && IsSeleVoxCommand(old);
        try { save(); }
        catch
        {
            if (!changed) throw;
            try
            {
                // Restore the exact prior path and value kind, including a migrated Portable command.
                var current = key.GetValue("SeleVox", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!Equals(current, previous))
                {
                    if (current is not null && (current is not string text || !IsSeleVoxCommand(text)))
                        throw new InvalidOperationException();
                    if (previous is null) key.DeleteValue("SeleVox", false);
                    else key.SetValue("SeleVox", previous, kind);
                }
            }
            catch { throw new StartupRegistrationException("设置保存失败，且启动项恢复失败；配置未更新，请检查 Windows 启动设置后重试。"); }
            throw;
        }
    }
    internal static void Apply(RegistryKey key, bool enabled, string? executable)
    {
        var existing = key.GetValue("SeleVox", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (!enabled)
        {
            if (existing is string old && IsSeleVoxCommand(old)) key.DeleteValue("SeleVox", false);
            return;
        }
        var command = Command(executable);
        if (existing is not null && (existing is not string text || !IsSeleVoxCommand(text)))
            throw new StartupRegistrationException("SeleVox 启动项已被其他命令占用，请先检查 Windows 启动设置。");
        if (!string.Equals(existing as string, command, StringComparison.Ordinal)) key.SetValue("SeleVox", command, RegistryValueKind.String);
    }
}
