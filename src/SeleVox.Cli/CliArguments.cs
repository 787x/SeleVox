using System.Text.Json;
using SeleVox.Core;
namespace SeleVox.Cli;
public static class CliArguments
{
    public const string Help = """
SeleVox.Cli (Windows x64) — JSON results; credentials remain in the desktop app.
  speak [--text TEXT | --file UTF8.txt | --stdin] [--preset NAME] [--wait]
  synthesize [text input] --output FILE.wav [--overwrite] [--wait]
  providers list | devices list | voices list [--provider NAME]
  presets list | presets show NAME | presets save NAME [TTS options] | presets delete NAME
  config show | config set [TTS options] [--allow-online true|false]
    [--export-directory PATH] [--expose-delete-preset true|false] [--expose-set-defaults true|false]
  jobs status ID | jobs stop ID | clones list | mcp
TTS options (omitted fields inherit defaults; no API URL/key overrides):
  --provider Windows|Azure|Edge|MiMo|OpenAiCompatible --voice ID --model ID
  --rate 0.5..2 --pitch -100..100 (Edge) --volume 0..1
  --devices '["device-id","default"]' (JSON; [] = system default)
  --style TEXT --tags '["温柔"]' (JSON; [] = clear)
  --mode Preset|VoiceDesign|VoiceClone --voice-description TEXT --clone-sample-id UUID
  --role TEXT --scene TEXT --direction TEXT --auto-segment true|false --segment-characters 50..3000
  --instructions TEXT (OpenAiCompatible)
  --parameters JSON_OBJECT (supports explicit null; no duplicate options)
  --json (all normal output is already JSON)
Default submission returns Job ID. --wait waits for completion; Ctrl+C cancels it.
Use jobs status/stop for submitted asynchronous jobs. synthesize never opens a playback device.
MCP returns submitted Job IDs; stop_job or process disconnection cancels its active job.
Exit: 0 success/submitted, 2 input, 3 permission, 4 execution/connection, 5 busy, 6 cancelled/timeout.
""";
    public static bool WantsHelp(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help") return true;
        for (int i = 1; i < args.Length; ++i)
        {
            if (args[i] == "--help") return true;
            if (args[i].StartsWith("--") && args[i] is not ("--wait" or "--overwrite" or "--stdin" or "--json")) ++i;
        }
        return false;
    }
    private static readonly Dictionary<string, string> Fields = new() {
        ["provider"]="provider", ["voice"]="voice", ["model"]="model", ["rate"]="rate", ["pitch"]="pitch", ["volume"]="volume", ["devices"]="devices", ["style"]="style", ["tags"]="tags", ["role"]="role", ["scene"]="scene", ["direction"]="direction", ["auto-segment"]="autoSegment", ["segment-characters"]="segmentCharacters", ["mode"]="mode", ["voice-description"]="voiceDescription", ["clone-sample-id"]="cloneSampleId", ["instructions"]="instructions" };
    public static async Task<(string Command, Dictionary<string, object?> Arguments)> ParseAsync(string[] args, CancellationToken token)
    {
        string command = args[0]; int index = 1;
        var a = new Dictionary<string, object?>(); var parameters = new Dictionary<string, JsonElement>(); var seen = new HashSet<string>();
        if (command is "providers" or "devices" or "voices" or "presets" or "config" or "jobs" or "clones")
        {
            if (index >= args.Length) throw new InvalidDataException();
            var action = args[index++];
            command = command switch { "providers" or "devices" or "voices" or "clones" when action == "list" => command,
                "presets" when action is "list" or "show" or "save" or "delete" => command + "." + action,
                "config" when action is "show" or "set" => command + "." + action,
                "jobs" when action is "status" or "stop" => command + "." + action, _ => throw new InvalidDataException() };
            if (command is "presets.show" or "presets.save" or "presets.delete" or "jobs.status" or "jobs.stop")
            { if (index >= args.Length) throw new InvalidDataException(); a[command.StartsWith("jobs") ? "id" : "name"] = args[index++]; }
        }
        else if (command is not ("speak" or "synthesize")) throw new InvalidDataException();
        string? text = null, file = null; bool stdin = false;
        while (index < args.Length)
        {
            var option = args[index++]; if (!option.StartsWith("--") || !seen.Add(option)) throw new InvalidDataException();
            if (option == "--json") continue;
            if (option is "--wait" or "--overwrite") { a[option[2..]] = true; continue; }
            if (option == "--stdin") { stdin = true; continue; }
            if (index >= args.Length) throw new InvalidDataException(); var value = args[index++]; var name = option[2..];
            switch (name)
            {
                case "text": text = value; break; case "file": file = value; break;
                case "preset": case "output": a[name] = value; break;
                case "allow-online": a["allowOnline"] = bool.Parse(value); break;
                case "export-directory": a["exportDirectory"] = Path.GetFullPath(value); break;
                case "expose-delete-preset": a["exposeDeletePreset"] = bool.Parse(value); break;
                case "expose-set-defaults": a["exposeSetDefaults"] = bool.Parse(value); break;
                case "parameters":
                    using (var document = JsonDocument.Parse(value)) { AgentParameters.Validate(document.RootElement); foreach (var field in document.RootElement.EnumerateObject()) if (!parameters.TryAdd(field.Name, field.Value.Clone())) throw new InvalidDataException(); } break;
                default:
                    if (!Fields.TryGetValue(name, out var key)) throw new InvalidDataException();
                    var parsed = key is "rate" or "pitch" or "volume" or "segmentCharacters" or "autoSegment" or "devices" or "tags" ? JsonDocument.Parse(value).RootElement.Clone() : AgentJson.Element(value);
                    if (!parameters.TryAdd(key, parsed)) throw new InvalidDataException(); break;
            }
        }
        if (command is "speak" or "synthesize")
        {
            if ((text is not null ? 1 : 0) + (file is not null ? 1 : 0) + (stdin ? 1 : 0) > 1) throw new InvalidDataException();
            if (file is not null)
            {
                if (new FileInfo(file).Length > 131072) throw new InvalidDataException();
                text = await File.ReadAllTextAsync(file, new System.Text.UTF8Encoding(false, true), token);
            }
            if (stdin || text is null && Console.IsInputRedirected)
            {
                var buffer = new char[4096]; var input = new System.Text.StringBuilder(); int count;
                while ((count = await Console.In.ReadAsync(buffer.AsMemory(), token)) > 0) { input.Append(buffer, 0, count); if (input.Length > 32768) throw new InvalidDataException(); }
                text = input.ToString();
            }
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32768) throw new InvalidDataException(); a["text"] = text;
            if (command == "synthesize" && a.TryGetValue("output", out var output)) a["output"] = Path.GetFullPath((string)output!);
        }
        else if (text is not null || file is not null || stdin) throw new InvalidDataException();
        if (parameters.Count > 0 || command is "presets.save" or "config.set")
        {
            var f = AgentJson.Element(parameters); AgentParameters.Validate(f);
            if (command == "voices" && parameters.Count == 1 && parameters.TryGetValue("provider", out var provider)) a["provider"] = provider.GetString();
            else a["parameters"] = f;
        }
        return (command, a);
    }
}
