using System.Text.Json;

namespace SeleVox.Core;

public sealed record ApplicationRule(string ExecutablePath, bool AutoSpeak = true);
public enum TtsProvider { Windows, Azure }

public sealed record Settings(string? VoiceId = null, double Rate = 1, float Volume = 0.8f, bool StartWithWindows = false,
    bool AutoSpeak = false, ApplicationRule[]? Applications = null, TtsProvider Provider = TtsProvider.Windows,
    string AzureRegion = "", string? AzureVoice = null, string[]? OutputDeviceIds = null,
    bool AllowAutomaticCloud = false)
{
    public const string DefaultOutput = "default";
    public bool IsValid => double.IsFinite(Rate) && Rate is >= 0.5 and <= 2 &&
                           float.IsFinite(Volume) && Volume is >= 0 and <= 1 &&
                           Enum.IsDefined(Provider) && AzureRegion is not null &&
                           (AzureRegion.Length == 0 || IsRegionValid(AzureRegion)) &&
                           (OutputDeviceIds is null || OutputDeviceIds.All(id => !string.IsNullOrWhiteSpace(id))) &&
                           (Applications is null || Applications.All(r => r is not null &&
                               !string.IsNullOrWhiteSpace(r.ExecutablePath) && Path.IsPathFullyQualified(r.ExecutablePath)));

    public static bool IsRegionValid(string region) => region.Length is > 0 and <= 64 &&
        region.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    public bool AllowsAutomatic(string? path) => AutoSpeak && !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && (Applications ?? []).Any(r => r.AutoSpeak &&
            string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
}

public sealed class SettingsStore(string path)
{
    public (Settings Settings, string? Warning) Load()
    {
        try
        {
            if (!File.Exists(path)) return (new(), "配置文件不存在，已使用默认设置。");
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            return settings is { IsValid: true } ? (settings, null) : (new(), "配置无效，已使用默认设置。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new(), "无法读取配置，已使用默认设置。");
        }
    }

    public void Save(Settings settings)
    {
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
