using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using SeleVox.Core;
namespace SeleVox.Cli;

[McpServerToolType]
public sealed class AgentTools(AgentClient client)
{
    private Task<CallToolResult> Call(string command, object args, CancellationToken token) => ToolResults.Call(client, command, args, token);
    [McpServerTool(Name = "speak_text"), Description("Speak using the desktop's single reading session. Returns a queryable Job ID immediately; stop_job or MCP process disconnection cancels the job. Omitted parameters inherit preset / Agent defaults / desktop settings. parameters fields: provider, voice, model, rate, pitch (Edge), volume, devices (IDs; [] = default), style, tags ([] clears), mode (Preset/VoiceDesign/VoiceClone), voiceDescription, cloneSampleId, role, scene, direction, autoSegment, segmentCharacters, instructions (OpenAI). No credentials or URL overrides. Online upload must be enabled by the user via CLI.")]
    public Task<CallToolResult> SpeakText(string text, string? preset = null, JsonElement? parameters = null, CancellationToken cancellationToken = default) =>
        Call("speak", new { text, preset, parameters = parameters ?? AgentJson.Empty, mcp = true, wait = false }, cancellationToken);
    [McpServerTool(Name = "synthesize_audio"), Description("Generate complete WAV without playback. output is a WAV filename or path inside the configured Agent export directory. Existing files require overwrite=true. Same parameters and inheritance as speak_text; cancellation cleans incomplete output. Returns job metadata and file path, never audio Base64.")]
    public Task<CallToolResult> SynthesizeAudio(string text, string output, string? preset = null, JsonElement? parameters = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        Call("synthesize", new { text, output, preset, parameters = parameters ?? AgentJson.Empty, overwrite, mcp = true, wait = false }, cancellationToken);
    [McpServerTool(Name = "list_tts_providers"), Description("List the five supported TTS providers.")]
    public Task<CallToolResult> Providers(CancellationToken cancellationToken) => Call("providers", new { }, cancellationToken);
    [McpServerTool(Name = "list_voices"), Description("List voices for the requested provider, or inherited default provider. Azure/Edge query their service only after Agent online authorization. OpenAI lists the desktop's configured voice; custom service catalog is unavailable.")]
    public Task<CallToolResult> Voices(string? provider = null, CancellationToken cancellationToken = default) => Call("voices", new { provider }, cancellationToken);
    [McpServerTool(Name = "list_audio_devices"), Description("List active Windows render device IDs plus system default. Does not change system devices.")]
    public Task<CallToolResult> Devices(CancellationToken cancellationToken) => Call("devices", new { }, cancellationToken);
    [McpServerTool(Name = "list_presets"), Description("List persistent Agent preset names.")]
    public Task<CallToolResult> Presets(CancellationToken cancellationToken) => Call("presets.list", new { }, cancellationToken);
    [McpServerTool(Name = "get_preset"), Description("Read one Agent preset. Presets contain configuration, never keys, input text or audio.")]
    public Task<CallToolResult> Preset(string name, CancellationToken cancellationToken) => Call("presets.show", new { name }, cancellationToken);
    [McpServerTool(Name = "save_preset"), Description("Create or replace a named Agent voice preset. parameters uses the same field names as speak_text. Does not change Agent defaults or cloud authorization.")]
    public Task<CallToolResult> SavePreset(string name, JsonElement parameters, CancellationToken cancellationToken) => Call("presets.save", new { name, parameters }, cancellationToken);
    [McpServerTool(Name = "get_settings"), Description("Read Agent defaults, presets, online permission and export directory. Never reveals credentials.")]
    public Task<CallToolResult> Settings(CancellationToken cancellationToken) => Call("config.show", new { }, cancellationToken);
    [McpServerTool(Name = "list_clone_samples"), Description("List local clone sample IDs and metadata; does not read or return sample audio.")]
    public Task<CallToolResult> Clones(CancellationToken cancellationToken) => Call("clones", new { }, cancellationToken);
    [McpServerTool(Name = "get_job_status"), Description("Query retained job metadata without generating audio again. States: queued, synthesizing, playing, completed, cancelled, failed.")]
    public Task<CallToolResult> Job(string id, CancellationToken cancellationToken) => Call("jobs.status", new { id }, cancellationToken);
    [McpServerTool(Name = "stop_job"), Description("Cancel only the given job, including synthesis/playback. A stale ID cannot stop its replacement.")]
    public Task<CallToolResult> Stop(string id, CancellationToken cancellationToken) => Call("jobs.stop", new { id }, cancellationToken);
}
[McpServerToolType]
public sealed class DeletePresetTool(AgentClient client)
{
    [McpServerTool(Name = "delete_preset"), Description("Delete one named Agent preset. Exposed only when the user enables this tool via CLI.")]
    public Task<CallToolResult> Delete(string name, CancellationToken cancellationToken) => ToolResults.Call(client, "presets.delete", new { name, mcp = true }, cancellationToken);
}
[McpServerToolType]
public sealed class SetDefaultsTool(AgentClient client)
{
    [McpServerTool(Name = "set_defaults"), Description("Merge TTS fields into persistent Agent defaults. Exposed only when enabled by the user via CLI. Cannot change credentials, service addresses, online permission, export directory or MCP exposure.")]
    public Task<CallToolResult> Set(JsonElement parameters, CancellationToken cancellationToken) => ToolResults.Call(client, "config.set", new { parameters, mcp = true }, cancellationToken);
}


internal static class ToolResults
{
    internal static async Task<CallToolResult> Call(AgentClient client, string command, object arguments, CancellationToken token)
    {
        AgentReply reply;
        try { reply = await client.CallAsync(command, arguments, token); }
        catch (OperationCanceledException) { reply = new(false, "cancelled_or_timeout", AgentJson.Empty); }
        catch (Exception) { reply = new(false, "connection_failed", AgentJson.Empty); }
        var element = AgentJson.Element(reply);
        var failedJob = reply.Data.ValueKind == JsonValueKind.Object && reply.Data.TryGetProperty("State", out var state) && state.GetString() == "failed";
        return new() { IsError = !reply.Success || failedJob, StructuredContent = element,
            Content = [new TextContentBlock { Text = element.GetRawText() }] };
    }
}
