using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SeleVox.App;
using SeleVox.Cli;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;
[Collection("WPF desktop")]
public sealed class VirtualMicrophoneAgentDesktopTests(ITestOutputHelper log)
{
    [VirtualMicAgentFact] public async Task PublishedCliAndMcpDefaultVirtualRouteExplicitOverrideAndExport()
    {
        var running = Process.GetProcessesByName("SeleVox.App");
        try { Assert.Empty(running); } finally { foreach (var process in running) process.Dispose(); }
        var cli = Environment.GetEnvironmentVariable("SELEVOX_AGENT_CLI")!;
        var app = Path.Combine(Path.GetDirectoryName(cli)!, "SeleVox.App.exe");
        Assert.True(File.Exists(app));
        var route = VirtualMicrophoneWindowsTests.ActualRoute();
        var physical = AudioOutputs.GetDevices().First(d => d.Label.Contains("Realtek"));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox");
        var settingsPath = Path.Combine(directory, "settings.json"); var agentPath = Path.Combine(directory, "agent.json");
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var agentBackup = File.Exists(agentPath) ? File.ReadAllBytes(agentPath) : null;
        var export = Path.Combine(Path.GetTempPath(), "SeleVox-pr17-agent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(export);
        Process? desktop = null;
        try
        {
            await VirtualMicrophoneTest.RunAsync(route, default);
            new SettingsStore(settingsPath).Save(new(OutputDeviceIds: [physical.Id], Volume: .1f) { VirtualMicrophoneEnabled = true, VirtualMicrophone = route });
            new AgentStore(agentPath).Update(_ => new() { ExportDirectory = export });
            desktop = Process.Start(new ProcessStartInfo(app) { ArgumentList = { "--agent-background" }, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            using var pipe = new AgentClient(AgentWire.PipeName);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            for (;;)
            {
                try { Assert.True((await pipe.CallAsync("providers", new { }, budget.Token)).Success); break; }
                catch (TimeoutException) { await Task.Delay(100, budget.Token); }
            }
            using var capture = new VirtualMicrophoneWindowsTests.Capture(route.CaptureId);
            var text = string.Concat(Enumerable.Repeat("Hello. This is the SeleVox virtual microphone test. ", 2));
            var speaking = await RunCli(cli, ["speak", "--text", text]);
            var speakingId = speaking.Data.GetProperty("Id").GetString()!;
            await WaitState(pipe, speakingId, "playing"); Assert.True(await capture.Rms() > .001);
            await WaitState(pipe, speakingId, "completed"); Assert.True(await capture.Rms() < .0005);
            var physicalOnly = await RunCli(cli, ["speak", "--text", text, "--devices", JsonSerializer.Serialize(new[] { physical.Id })]);
            var physicalId = physicalOnly.Data.GetProperty("Id").GetString()!;
            await WaitState(pipe, physicalId, "playing"); Assert.True(await capture.Rms() < .0005);
            await WaitState(pipe, physicalId, "completed");
            await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = cli, Arguments = ["mcp"], Name = "SeleVox PR17" }));
            var reply = Result(await mcp.CallToolAsync("speak_text", new Dictionary<string, object?> { ["text"] = text })); Assert.True(reply.Success);
            var id = reply.Data.GetProperty("Id").GetString()!;
            await WaitState(pipe, id, "playing"); Assert.True(await capture.Rms() > .001);
            Assert.True(Result(await mcp.CallToolAsync("stop_job", new Dictionary<string, object?> { ["id"] = id })).Success);
            await Task.Delay(250); Assert.True(await capture.Rms() < .0005);
            var generated = RunCli(cli, ["synthesize", "--text", "Hello. File export only.", "--output", Path.Combine(export, "test.wav"), "--wait"]);
            Assert.True(await capture.Rms() < .0005); Assert.Equal("completed", (await generated).Data.GetProperty("State").GetString()); Assert.True(await capture.Rms() < .0005);
            using var wave = new NAudio.Wave.WaveFileReader(Path.Combine(export, "test.wav")); Assert.True(wave.Length > 1000);
            log.WriteLine("Actual published CLI + MCP stdio + Windows local TTS: inherited main config reached real virtual Capture; explicit physical CLI override and WAV export were silent on Capture; MCP Stop stopped Capture audio. Test settings restored.");
        }
        finally
        {
            if (desktop is not null) { if (!desktop.HasExited) { desktop.Kill(); await desktop.WaitForExitAsync(); } desktop.Dispose(); }
            Restore(settingsPath, settingsBackup); Restore(agentPath, agentBackup); Directory.Delete(export, true);
        }
    }
    private static void Restore(string path, byte[]? backup) { if (backup is null) File.Delete(path); else File.WriteAllBytes(path, backup); }
    private static async Task WaitState(AgentClient pipe, string id, string desired)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            var reply = await pipe.CallAsync("jobs.status", new { id }, timeout.Token); Assert.True(reply.Success);
            var state = reply.Data.GetProperty("State").GetString(); if (state == desired) return;
            Assert.DoesNotContain(state, new[] { "failed", "cancelled", "completed" }); await Task.Delay(50, timeout.Token);
        }
    }
    private static AgentReply Result(CallToolResult result) => JsonSerializer.Deserialize<AgentReply>(result.Content.OfType<TextContentBlock>().Single().Text, AgentJson.Options)!;
    private static async Task<AgentReply> RunCli(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); Assert.Equal(0, process.ExitCode); await stderr; return JsonSerializer.Deserialize<AgentReply>(await stdout, AgentJson.Options)!; }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
public sealed class VirtualMicAgentFactAttribute : FactAttribute
{
    public VirtualMicAgentFactAttribute()
    { if (Environment.GetEnvironmentVariable("SELEVOX_VIRTUAL_MIC_TESTS") != "1" || Environment.GetEnvironmentVariable("SELEVOX_AGENT_CLI") is null) Skip = "Opt-in published CLI/MCP, no running SeleVox; temporarily saves and restores user settings."; }
}
