using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeleVox.Core;

// JSON object membership distinguishes absent fields from explicit null/empty values.
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
    public static JsonElement Empty => Element(new { });
}
public sealed record AgentRequest(int Version = 1, string Command = "", JsonElement Arguments = default);
public sealed record AgentReply(bool Success, string Code, JsonElement Data);
public sealed record AgentConfiguration
{
    public JsonElement Defaults { get; init; } = AgentJson.Empty;
    public Dictionary<string, JsonElement> Presets { get; init; } = new(StringComparer.Ordinal);
    public bool ExposeDeletePreset { get; init; }
    public bool ExposeSetDefaults { get; init; }
    public string ExportDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "agent-exports");
}
public sealed class AgentStore(string path)
{
    public AgentConfiguration Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
        // Ignore only the two retired permission fields. Other unknown configuration fields remain errors.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        using var normalized = new MemoryStream();
        using (var writer = new Utf8JsonWriter(normalized))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is not ("AllowOnline" or "AuthorizedOpenAiAddress")) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        var config = JsonSerializer.Deserialize<AgentConfiguration>(normalized.ToArray(), AgentJson.Options) ?? throw new InvalidDataException();
        Validate(config); return config;
    }
    public AgentConfiguration Update(Func<AgentConfiguration, AgentConfiguration> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // OS releases this exclusive lease even when a process crashes. No stale .lock ownership.
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var next = change(Load()); Validate(next);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(next, AgentJson.Options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return next;
    }
    public static void Validate(AgentConfiguration c)
    {
        if (c.Presets is null || c.ExportDirectory is null) throw new InvalidDataException();
        if (JsonSerializer.SerializeToUtf8Bytes(c, AgentJson.Options).Length > 128 * 1024) throw new InvalidDataException();
        AgentParameters.Validate(c.Defaults);
        if (c.Presets.Count > 100 || !Path.IsPathFullyQualified(c.ExportDirectory) || c.ExportDirectory.Length > 240) throw new InvalidDataException();
        foreach (var p in c.Presets) { ValidateName(p.Key); AgentParameters.Validate(p.Value); }
    }
    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')) throw new InvalidDataException();
    }
}
public static class AgentParameters
{
    public static readonly string[] Names = ["provider", "voice", "model", "rate", "pitch", "volume", "devices", "style", "tags", "role", "scene", "direction", "autoSegment", "segmentCharacters", "mode", "voiceDescription", "cloneSampleId", "instructions"];
    public static void Validate(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>();
        foreach (var p in fields.EnumerateObject())
        {
            if (!seen.Add(p.Name) || !Names.Contains(p.Name)) throw new InvalidDataException();
            if (p.Value.ValueKind == JsonValueKind.Null) continue;
            switch (p.Name)
            {
                case "rate": if (!p.Value.TryGetDouble(out var rate) || !double.IsFinite(rate) || rate is < .5 or > 2) throw new InvalidDataException(); break;
                case "volume": if (!p.Value.TryGetSingle(out var volume) || !float.IsFinite(volume) || volume is < 0 or > 1) throw new InvalidDataException(); break;
                case "pitch": if (!p.Value.TryGetInt32(out var pitch) || pitch is < -100 or > 100) throw new InvalidDataException(); break;
                case "segmentCharacters": if (!p.Value.TryGetInt32(out var size) || size is < 50 or > 3000) throw new InvalidDataException(); break;
                case "autoSegment": if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException(); break;
                case "devices": case "tags":
                    if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() > 32 || p.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString()!.Length is < 1 or > 1024)) throw new InvalidDataException(); break;
                default: if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > (p.Name == "instructions" ? 4096 : 1000)) throw new InvalidDataException(); break;
            }
        }
        if (fields.TryGetProperty("provider", out var provider) && provider.ValueKind != JsonValueKind.Null &&
            (!Enum.TryParse<TtsProvider>(provider.GetString(), false, out var parsed) || !Enum.IsDefined(parsed))) throw new InvalidDataException();
        if (fields.TryGetProperty("mode", out var mode) && mode.ValueKind != JsonValueKind.Null && mode.GetString() is not ("Preset" or "VoiceDesign" or "VoiceClone")) throw new InvalidDataException();
        if (fields.TryGetProperty("cloneSampleId", out var clone) && clone.ValueKind != JsonValueKind.Null && clone.GetString() != "" && !Guid.TryParse(clone.GetString(), out _)) throw new InvalidDataException();
    }
    public static JsonElement Merge(params JsonElement[] layers)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (var layer in layers) { Validate(layer); foreach (var p in layer.EnumerateObject()) values[p.Name] = p.Value.Clone(); }
        return AgentJson.Element(values);
    }
    public static (Settings Settings, string? CloneId) Resolve(Settings main, AgentConfiguration c, string? preset, JsonElement explicitFields)
    {
        var selected = preset is null ? AgentJson.Empty : c.Presets.TryGetValue(preset, out var found) ? found : throw new InvalidDataException();
        var f = Merge(c.Defaults, selected, explicitFields);
        string? Str(string name, string? fallback) => f.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Null ? null : v.GetString() : fallback;
        double Num(string name, double fallback) => f.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Null ? name switch { "rate" => 1, "volume" => .8, "pitch" => 0, "segmentCharacters" => 3000, _ => fallback } : v.GetDouble() : fallback;
        string[]? Array(string name, string[]? fallback) => f.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Null ? [] : v.EnumerateArray().Select(x => x.GetString()!).ToArray() : fallback;
        var provider = Enum.Parse<TtsProvider>(Str("provider", main.Provider.ToString()) ?? "Windows");
        var s = main with { Provider = provider, Rate = Num("rate", main.Rate), Volume = (float)Num("volume", main.Volume),
            OutputDeviceIds = Array("devices", main.OutputDeviceIds), MiMoStreaming = false,
            VirtualMicrophoneEnabled = !f.TryGetProperty("devices", out _) && main.VirtualMicrophoneEnabled,
            ExplicitOutputDevices = f.TryGetProperty("devices", out _),
            StrictOutputDevices = f.TryGetProperty("devices", out _) || !main.VirtualMicrophoneEnabled };
        var voice = Str("voice", provider switch { TtsProvider.Windows => main.VoiceId, TtsProvider.Azure => main.AzureVoice, TtsProvider.Edge => main.EdgeVoice, TtsProvider.MiMo => main.MiMoVoice, _ => main.OpenAiVoice });
        var advanced = new[] { "style", "tags", "role", "scene", "direction", "autoSegment", "segmentCharacters", "mode", "voiceDescription", "cloneSampleId" };
        if (provider != TtsProvider.MiMo && advanced.Any(n => explicitFields.TryGetProperty(n, out _)) || provider != TtsProvider.Edge && explicitFields.TryGetProperty("pitch", out _) ||
            provider != TtsProvider.OpenAiCompatible && explicitFields.TryGetProperty("instructions", out _) || provider is TtsProvider.Windows or TtsProvider.Azure or TtsProvider.Edge && explicitFields.TryGetProperty("model", out _)) throw new InvalidDataException();
        s = provider switch {
            TtsProvider.Windows => s with { VoiceId = string.IsNullOrEmpty(voice) ? null : voice }, TtsProvider.Azure => s with { AzureVoice = voice },
            TtsProvider.Edge => s with { EdgeVoice = voice, EdgePitchHz = (int)Num("pitch", main.EdgePitchHz) },
            TtsProvider.OpenAiCompatible => s with { OpenAiVoice = voice ?? "", OpenAiModel = Str("model", main.OpenAiModel) ?? "", OpenAiInstructions = Str("instructions", main.OpenAiInstructions) ?? "" },
            _ => s with { MiMoVoice = voice ?? "mimo_default", MiMoModel = Str("model", main.MiMoModel) ?? "mimo-v2.5-tts", MiMoStyle = Str("style", main.MiMoStyle) ?? "",
                MiMoOverallStyles = Array("tags", main.MiMoOverallStyles), MiMoVoiceDescription = Str("voiceDescription", main.MiMoVoiceDescription) ?? "",
                MiMoAutoSegment = f.TryGetProperty("autoSegment", out var seg) ? seg.ValueKind == JsonValueKind.Null || seg.GetBoolean() : main.MiMoAutoSegment,
                MiMoSegmentCharacters = (int)Num("segmentCharacters", main.MiMoSegmentCharacters) } };
        if (provider == TtsProvider.MiMo)
        {
            // Mode and model describe the same choice; resolve both at each priority layer.
            var model = main.MiMoModel;
            foreach (var layer in new[] { c.Defaults, selected, explicitFields })
            {
                string? byMode = null;
                if (layer.TryGetProperty("mode", out var mode)) byMode = (mode.ValueKind == JsonValueKind.Null ? null : mode.GetString()) switch
                    { "VoiceDesign" => "mimo-v2.5-tts-voicedesign", "VoiceClone" => "mimo-v2.5-tts-voiceclone", _ => "mimo-v2.5-tts" };
                if (layer.TryGetProperty("model", out var value))
                {
                    var byModel = value.ValueKind == JsonValueKind.Null ? "mimo-v2.5-tts" : value.GetString()!;
                    if (byMode is not null && byMode != byModel) throw new InvalidDataException();
                    model = byModel;
                }
                else if (byMode is not null) model = byMode;
            }
            s = s with { MiMoModel = model };
            var director = string.Join("\n", new[] { ("角色", Str("role", null)), ("场景", Str("scene", null)), ("指导", Str("direction", null)) }.Where(x => !string.IsNullOrWhiteSpace(x.Item2)).Select(x => x.Item1 + "：" + x.Item2!.Trim()));
            if (director.Length > 0) s = s with { MiMoStyle = string.Join("\n", new[] { s.MiMoStyle, director }.Where(x => x.Length > 0)) };
        }
        if (!s.IsValid || s.MiMoSegmentCharacters is < 50 or > 3000 || s.MiMoVoiceDescription.Length > 1000) throw new InvalidDataException();
        return (s, Str("cloneSampleId", null));
    }
}
