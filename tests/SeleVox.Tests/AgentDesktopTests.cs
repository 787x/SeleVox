using SeleVox.App;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SeleVox.Cli;
using SeleVox.Core;
using NAudio.Wave;
namespace SeleVox.Tests;
public sealed class AgentDesktopFact : FactAttribute
{ public AgentDesktopFact() { if (Environment.GetEnvironmentVariable("SELEVOX_AGENT_TESTS") != "1") Skip = "Set SELEVOX_AGENT_TESTS=1 with published CLI path to run real desktop/MCP checks."; } }
public sealed class AgentColdFact : FactAttribute
{ public AgentColdFact() { if (Environment.GetEnvironmentVariable("SELEVOX_AGENT_COLD_TESTS") != "1") Skip = "Requires published CLI and no running desktop instance; set SELEVOX_AGENT_COLD_TESTS=1."; } }
[Collection("WPF desktop")]
public sealed class AgentDesktopTests
{
    private static string Executable => Environment.GetEnvironmentVariable("SELEVOX_AGENT_CLI") ?? throw new InvalidOperationException();
    private static AgentReply Result(CallToolResult result) => JsonSerializer.Deserialize<AgentReply>(result.Content.OfType<TextContentBlock>().Single().Text, AgentJson.Options)!;
    private static async Task<AgentReply> Wait(AgentClient client, string id)
    {
        using var timeout = new CancellationTokenSource(30000);
        while (true) { var reply = await client.CallAsync("jobs.status", new { id }, timeout.Token); if (reply.Data.GetProperty("State").GetString() is "completed" or "failed" or "cancelled") return reply; await Task.Delay(100, timeout.Token); }
    }
    [AgentColdFact] public async Task ColdCliRedirectedOutputExitsWhileBackgroundStaysHidden()
    {
        var old = System.Diagnostics.Process.GetProcessesByName("SeleVox.App");
        try { Assert.Empty(old); } finally { foreach (var process in old) process.Dispose(); }
        var foreground = Native.GetForegroundWindow();
        using var cli = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Executable)
            { ArgumentList = { "providers", "list" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true });
        try
        {
            var stdout = cli!.StandardOutput.ReadToEndAsync();
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var reply = JsonSerializer.Deserialize<AgentReply>(await stdout.WaitAsync(TimeSpan.FromSeconds(3)), AgentJson.Options)!;
            Assert.Equal(0, cli.ExitCode); Assert.True(reply.Success); Assert.Equal(foreground, Native.GetForegroundWindow());
            var apps = System.Diagnostics.Process.GetProcessesByName("SeleVox.App");
            try { var app = Assert.Single(apps); Assert.Equal(0, app.MainWindowHandle); } finally { foreach (var process in apps) process.Dispose(); }
            using var second = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Executable)!, "SeleVox.App.exe"))
                { ArgumentList = { "--agent-background" }, UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            await second!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(foreground, Native.GetForegroundWindow());
            apps = System.Diagnostics.Process.GetProcessesByName("SeleVox.App");
            try { Assert.Equal(0, Assert.Single(apps).MainWindowHandle); } finally { foreach (var process in apps) process.Dispose(); }
        }
        finally { if (!cli!.HasExited) cli.Kill(); }
    }
    [AgentDesktopFact] public async Task RealMcpStdioDiscoveryCallsExportCancelAndExposure()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "agent.json");
        var backup = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        var directory = Path.Combine(Path.GetTempPath(), "SeleVox-Agent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var cli = new AgentClient();
        try
        {
            Assert.True((await cli.CallAsync("config.set", new { parameters = new { provider = "Windows", voice = (string?)null, devices = Array.Empty<string>() }, exportDirectory = directory, exposeDeletePreset = false, exposeSetDefaults = false })).Success);
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = Executable, Arguments = ["mcp"], Name = "SeleVox" })))
            {
                var tools = await client.ListToolsAsync(); Assert.Equal(12, tools.Count); Assert.DoesNotContain(tools, t => t.Name is "delete_preset" or "set_defaults");
                foreach (var tool in new[] { "list_tts_providers", "list_audio_devices", "list_voices", "list_presets", "get_settings", "list_clone_samples" }) Assert.True(Result(await client.CallToolAsync(tool)).Success);
                var save = Result(await client.CallToolAsync("save_preset", new Dictionary<string, object?> { ["name"] = "PR14-test", ["parameters"] = new { provider = "Windows", rate = 1.2, voice = (string?)null } })); Assert.True(save.Success);
                Assert.True(Result(await client.CallToolAsync("get_preset", new Dictionary<string, object?> { ["name"] = "PR14-test" })).Success);
                var generated = Result(await client.CallToolAsync("synthesize_audio", new Dictionary<string, object?> { ["text"] = "你好，测试声音。Hello.", ["output"] = "mcp.wav", ["preset"] = "PR14-test" })); Assert.True(generated.Success);
                var id = generated.Data.GetProperty("Id").GetString()!; var done = await Wait(cli, id); Assert.Equal("completed", done.Data.GetProperty("State").GetString());
                using (var wave = new WaveFileReader(Path.Combine(directory, "mcp.wav"))) Assert.True(wave.Length > 1000);
                Assert.True(Result(await client.CallToolAsync("get_job_status", new Dictionary<string, object?> { ["id"] = id })).Success);
                var denied = Result(await client.CallToolAsync("synthesize_audio", new Dictionary<string, object?> { ["text"] = "fixture", ["output"] = "../escape.wav" })); Assert.False(denied.Success);
                var speak = Result(await client.CallToolAsync("speak_text", new Dictionary<string, object?> { ["text"] = string.Concat(Enumerable.Repeat("这是停止测试。", 20)) })); Assert.True(speak.Success);
                var speakingId = speak.Data.GetProperty("Id").GetString()!;
                Assert.True(Result(await client.CallToolAsync("stop_job", new Dictionary<string, object?> { ["id"] = speakingId })).Success);
                Assert.Equal("cancelled", (await Wait(cli, speakingId)).Data.GetProperty("State").GetString());
            }
            Assert.True((await cli.CallAsync("config.set", new { exposeDeletePreset = true, exposeSetDefaults = true })).Success);
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = Executable, Arguments = ["mcp"], Name = "SeleVox" })))
            {
                Assert.Equal(14, (await client.ListToolsAsync()).Count);
                Assert.True(Result(await client.CallToolAsync("set_defaults", new Dictionary<string, object?> { ["parameters"] = new { rate = 1.1 } })).Success);
                Assert.True(Result(await client.CallToolAsync("delete_preset", new Dictionary<string, object?> { ["name"] = "PR14-test" })).Success);
                var submit = Result(await client.CallToolAsync("speak_text", new Dictionary<string, object?> { ["text"] = string.Concat(Enumerable.Repeat("这是断开测试。", 100)) }));
                Assert.True(submit.Success); var id = submit.Data.GetProperty("Id").GetString()!;
                await client.DisposeAsync(); Assert.Equal("cancelled", (await Wait(cli, id)).Data.GetProperty("State").GetString());
            }
        }
        finally { if (backup is null) File.Delete(path); else await File.WriteAllBytesAsync(path, backup); Directory.Delete(directory, true); }
    }
    [AgentDesktopFact] public async Task RawStdioContainsOnlyProtocolAndErrorsAreRedacted()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Executable)
            { ArgumentList = { "mcp" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
        try
        {
            async Task<JsonElement> Request(object request)
            {
                await process!.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(request));
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.NotNull(line);
                return System.Text.Json.JsonDocument.Parse(line).RootElement.Clone();
            }
            var init = await Request(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "PR14-test", version = "1" } } });
            Assert.Equal("2.0", init.GetProperty("jsonrpc").GetString()); Assert.Equal(1, init.GetProperty("id").GetInt32()); Assert.True(init.TryGetProperty("result", out _));
            await process!.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var list = await Request(new { jsonrpc = "2.0", id = 2, method = "tools/list" }); Assert.True(list.GetProperty("result").GetProperty("tools").GetArrayLength() >= 12);
            const string secret = "SELEVOX_PRIVATE_SENTINEL";
            var denied = await Request(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "speak_text", arguments = new { text = secret, parameters = new { apiKey = secret } } } });
            Assert.DoesNotContain(secret, denied.GetRawText()); Assert.True(denied.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal("invalid_input", denied.GetProperty("result").GetProperty("structuredContent").GetProperty("Code").GetString());
        }
        finally { if (!process!.HasExited) process.Kill(); await process.WaitForExitAsync(); Assert.DoesNotContain("SELEVOX_PRIVATE_SENTINEL", await process.StandardError.ReadToEndAsync()); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [AgentDesktopFact] public async Task DesktopStopMessageCancelsRealAgentPlayback()
    {
        using var cli = new AgentClient();
        var speak = await cli.CallAsync("speak", new { text = string.Concat(Enumerable.Repeat("This is the desktop stop test. ", 30)), parameters = new { provider = "Windows", voice = (string?)null, devices = Array.Empty<string>(), volume = .1 } });
        Assert.True(speak.Success); var id = speak.Data.GetProperty("Id").GetString()!;
        using var timeout = new CancellationTokenSource(15000);
        while ((await cli.CallAsync("jobs.status", new { id }, timeout.Token)).Data.GetProperty("State").GetString() != "playing") await Task.Delay(100, timeout.Token);
        nint receiver = 0;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("SeleVox.App"))
        {
            using (process)
            {
                for (nint window = FindWindowEx(new(-3), 0, null, "SeleVox hotkeys"); window != 0; window = FindWindowEx(new(-3), window, null, "SeleVox hotkeys"))
                { Native.GetWindowThreadProcessId(window, out var pid); if (pid == process.Id) { receiver = window; break; } }
            }
        }
        Assert.NotEqual(0, receiver); Assert.True(PostMessage(receiver, 0x0312, 3, 0));
        Assert.Equal("cancelled", (await Wait(cli, id)).Data.GetProperty("State").GetString());
    }
    [AgentDesktopFact] public async Task RealWindowsSingleDualPlaybackAndStrictDeviceFailure()
    {
        using var cli = new AgentClient(); var devices = await cli.CallAsync("devices", new { });
        var ids = devices.Data.EnumerateArray().OrderBy(d => d.GetProperty("Label").GetString()!.Contains("Realtek") ? 0 : 1).Select(d => d.GetProperty("Id").GetString()!).Where(d => d != "default").Take(2).ToArray(); Assert.Equal(2, ids.Length);
        foreach (var outputs in new[] { new[] { "default" }, new[] { ids[0] }, ids })
        {
            var spoken = await cli.CallAsync("speak", new { text = "你好，这是语音测试。", parameters = new { provider = "Windows", voice = (string?)null, devices = outputs, volume = .1 }, wait = true });
            Assert.True(spoken.Success); Assert.Equal("completed", spoken.Data.GetProperty("State").GetString());
        }
        var invalid = await cli.CallAsync("speak", new { text = "fixture", parameters = new { provider = "Windows", devices = new[] { "invalid" } } }); Assert.False(invalid.Success); Assert.Equal("device_unavailable", invalid.Code);
    }
}
