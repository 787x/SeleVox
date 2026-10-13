using System.Text.Json;

namespace SeleVox.Core;

public sealed record ApplicationRule(string ExecutablePath, bool AutoSpeak = true);
public enum TtsProvider { Windows = 0, Azure = 1, Edge = 2, MiMo = 3, OpenAiCompatible = 4 }

public sealed record Settings(string? VoiceId = null, double Rate = 1, float Volume = 0.8f, bool StartWithWindows = false,
    bool AutoSpeak = false, ApplicationRule[]? Applications = null, TtsProvider Provider = TtsProvider.Windows,
    string AzureRegion = "", string? AzureVoice = null, string[]? OutputDeviceIds = null,
    bool AllowAutomaticCloud = false, string? EdgeVoice = null, int EdgePitchHz = 0,
    bool AllowAutomaticEdge = false, string EdgeLocaleFilter = "zh-CN",
    string MiMoVoice = "冰糖", string MiMoStyle = "", bool AllowAutomaticMiMo = false, string MiMoModel = "mimo-v2.5-tts",
    string OpenAiBaseUrl = "https://api.openai.com/v1", string OpenAiModel = "gpt-4o-mini-tts",
    string OpenAiVoice = "coral", string OpenAiInstructions = "", bool AllowAutomaticOpenAi = false,
    string? OpenAiAuthorizedBaseUrl = null, bool MiMoStreaming = true, OcrLanguage OcrLanguage = OcrLanguage.ChineseEnglish,
    string[]? MiMoOverallStyles = null)
{
    public bool UsesMiMoStreaming => Provider == TtsProvider.MiMo && MiMoStreaming && MiMoModel == "mimo-v2.5-tts";
    public const string DefaultOutput = "default";
    public bool IsValid => double.IsFinite(Rate) && Rate is >= 0.5 and <= 2 &&
                           float.IsFinite(Volume) && Volume is >= 0 and <= 1 &&
                           Enum.IsDefined(Provider) && Enum.IsDefined(OcrLanguage) && EdgePitchHz is >= -100 and <= 100 && EdgeLocaleFilter is not null && AzureRegion is not null &&
                           MiMoVoices.Contains(MiMoVoice) && MiMoStyle is { Length: <= 1000 } && MiMoStyleTags.IsValid(MiMoOverallStyles) && !string.IsNullOrWhiteSpace(MiMoModel) && MiMoModel.Length <= 200 &&
                           NormalizeServiceUrl(OpenAiBaseUrl) is not null && !string.IsNullOrWhiteSpace(OpenAiModel) && OpenAiModel.Length <= 200 &&
                           !string.IsNullOrWhiteSpace(OpenAiVoice) && OpenAiVoice.Length <= 200 && OpenAiInstructions is { Length: <= 4096 } &&
                           (AzureRegion.Length == 0 || IsRegionValid(AzureRegion)) &&
                           (OutputDeviceIds is null || OutputDeviceIds.All(id => !string.IsNullOrWhiteSpace(id))) &&
                           (Applications is null || Applications.All(r => r is not null &&
                               !string.IsNullOrWhiteSpace(r.ExecutablePath) && Path.IsPathFullyQualified(r.ExecutablePath)));

    public static bool IsRegionValid(string region) => region.Length is > 0 and <= 64 &&
        region.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    public static IReadOnlyList<string> MiMoVoices { get; } = Array.AsReadOnly(new[] { "mimo_default", "冰糖", "茉莉", "苏打", "白桦", "Mia", "Chloe", "Milo", "Dean" });

    // This exact canonical base is both the credential namespace and automatic-consent identity.
    public static string? NormalizeServiceUrl(string? value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public bool AllowsAutomaticProvider => Provider switch
    {
        TtsProvider.Azure => AllowAutomaticCloud,
        TtsProvider.Edge => AllowAutomaticEdge,
        TtsProvider.MiMo => AllowAutomaticMiMo,
        TtsProvider.OpenAiCompatible => AllowAutomaticOpenAi && NormalizeServiceUrl(OpenAiBaseUrl) is { } address &&
            address == OpenAiAuthorizedBaseUrl,
        TtsProvider.Windows => true,
        _ => false
    };

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
