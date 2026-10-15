using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;
using SeleVox.Cli;
using NAudio.Wave;
namespace SeleVox.Tests;
[Trait("Category", "Offline")]
public sealed class AgentExecutionTests
{
    private sealed class Speech(Func<CancellationToken, Task<Stream>> run) : ISpeechSynthesis
    { public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation) => run(cancellation); }
    private sealed class NoPlayback : IAudioPlaybackFactory { public IAudioPlayback CreatePlayback(Stream audio, Settings settings) => throw new InvalidOperationException("unexpected playback"); }
    private sealed class Outputs : IAudioPlaybackFactory
    {
        public List<Player> Players = new();
        public IAudioPlayback CreatePlayback(Stream audio, Settings settings) { var player = new Player(audio); Players.Add(player); return player; }
    }
    private sealed class Player(Stream audio) : IAudioPlayback
    {
        public bool Stopped;
        public event Action<Exception?>? Finished;
        public void Play() { } public void Pause() { } public void Stop() { Stopped = true; Finished?.Invoke(null); }
        public void Dispose() { audio.Dispose(); }
    }
    private static Stream Wave()
    {
        var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), new WaveFormat(24000, 16, 1))) writer.Write(new byte[4800]);
        stream.Position = 0; return stream;
    }
    private static AgentRequest R(string command, object args) => new(1, command, AgentJson.Element(args));
    private static async Task<AgentJob> Status(AgentExecution execution, string id) => (AgentJob)await execution.ExecuteAsync(R("jobs.status", new { id }), default);
    [Fact] public async Task DiagnosticCancelDoesNotCancelActiveAgentExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pr15-agent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new Speech(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Wave(); });
        using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(), action => action(), directory);
        try
        {
            var job = (AgentJob)await execution.ExecuteAsync(R("synthesize", new { text = "agent fixture", output = Path.Combine(directory, "out.wav") }), default); await entered.Task;
            var diag = ProviderDiagnosticsTests.Fake(async (_, _, token) => { await Task.Delay(-1, token); throw new Exception(); });
            using var cancel = new CancellationTokenSource(); var pending = diag.RunAsync(ProviderDiagnosticsTests.Snapshot(DiagnosticService.Windows), cancel.Token);
            cancel.Cancel(); Assert.Equal(DiagnosticState.Canceled, (await pending).State);
            Assert.Equal("synthesizing", (await Status(execution, job.Id)).State);
            release.TrySetResult(); await execution.WaitForJobAsync(job.Id, default); Assert.Equal("completed", (await Status(execution, job.Id)).State);
            using var wave = new WaveFileReader(Path.Combine(directory, "out.wav")); Assert.Equal(4800, wave.Length);
        }
        finally { release.TrySetResult(); Directory.Delete(directory, true); }
    }
    [Fact] public async Task BoundedJobsCancelStaleStopAndSaveOnceWithoutDevice()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var calls = 0; var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new Speech(async token => { Interlocked.Increment(ref calls); await pending.Task.WaitAsync(token); return Wave(); });
        using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(OutputDeviceIds: ["invalid-device"]), action => action(), directory);
        try
        {
            var output = Path.Combine(directory, "out.wav");
            var first = (AgentJob)await execution.ExecuteAsync(R("synthesize", new { text = "fixture", output }), default);
            Assert.Equal("synthesizing", first.State);
            await Assert.ThrowsAsync<AgentBusyException>(() => execution.ExecuteAsync(R("synthesize", new { text = "fixture", output }), default));
            await execution.ExecuteAsync(R("jobs.stop", new { id = first.Id }), default);
            await execution.WaitForJobAsync(first.Id, default); Assert.Equal("cancelled", (await Status(execution, first.Id)).State); Assert.False(File.Exists(output));
            var second = (AgentJob)await execution.ExecuteAsync(R("synthesize", new { text = "fixture", output }), default);
            await execution.ExecuteAsync(R("jobs.stop", new { id = first.Id }), default);
            Assert.Equal("synthesizing", (await Status(execution, second.Id)).State);
            pending.SetResult(); await execution.WaitForJobAsync(second.Id, default);
            Assert.Equal("completed", (await Status(execution, second.Id)).State); Assert.Equal(2, calls);
            using var wave = new WaveFileReader(output); Assert.Equal(4800, wave.Length); wave.Dispose();
            await Status(execution, second.Id); Assert.Equal(2, calls);
            await Assert.ThrowsAsync<IOException>(() => execution.ExecuteAsync(R("synthesize", new { text = "fixture", output }), default));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task BoundedHistoryRetainsTheMostRecentJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var speech = new Speech(_ => Task.FromResult(Wave())); using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(), action => action(), directory);
        var ids = new List<string>();
        try
        {
            for (int i = 0; i < 130; ++i)
            {
                var job = (AgentJob)await execution.ExecuteAsync(R("synthesize", new { text = "fixture", output = Path.Combine(directory, "out.wav"), overwrite = true, wait = true }), default);
                Assert.Equal("completed", job.State); ids.Add(job.Id);
            }
            foreach (var id in ids.Take(2)) await Assert.ThrowsAsync<InvalidDataException>(() => Status(execution, id));
            foreach (var id in ids.Skip(2)) Assert.Equal("completed", (await Status(execution, id)).State);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task PipeValidationAndMcpDisconnectCancelJob()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new Speech(async token => { await pending.Task.WaitAsync(token); return Wave(); });
        using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(), action => action(), directory);
        var name = "SeleVox-Test-" + Guid.NewGuid().ToString("N"); using var server = new AgentPipe(execution, name);
        try
        {
            var client = new AgentClient(name);
            Assert.False((await client.CallAsync("unknown", new { })).Success);
            var output = Path.Combine(directory, "out.wav");
            new AgentStore(Path.Combine(directory, "agent.json")).Update(c => c with { ExportDirectory = directory });
            var reply = await client.CallAsync("synthesize", new { text = "fixture", output, mcp = true }); Assert.True(reply.Success);
            var id = reply.Data.GetProperty("Id").GetString()!;
            client.Dispose(); await execution.WaitForJobAsync(id, new CancellationTokenSource(5000).Token);
            Assert.Equal("cancelled", (await Status(execution, id)).State); Assert.False(File.Exists(output));
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000); await AgentWire.WriteAsync(pipe, new AgentRequest(2, "providers", AgentJson.Empty), default);
            var invalid = await AgentWire.ReadAsync<AgentReply>(pipe, default); Assert.False(invalid.Success); Assert.Equal("invalid_input", invalid.Code);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task OnlineSynthesisNeedsNoAgentPermissionAndMcpExposureRemainsRestricted()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var speech = new Speech(_ => Task.FromResult(Wave())); using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(Provider: TtsProvider.MiMo), action => action(), directory);
        try
        {
            var online = (AgentJob)await execution.ExecuteAsync(R("synthesize", new { text = "fixture", output = Path.Combine(directory, "x.wav"), wait = true }), default); Assert.Equal("completed", online.State);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => execution.ExecuteAsync(R("config.set", new { parameters = new { rate = 1.1 }, mcp = true }), default));
            await execution.ExecuteAsync(R("config.set", new { exposeSetDefaults = true, exposeDeletePreset = true }), default);
            await execution.ExecuteAsync(R("config.set", new { parameters = new { rate = 1.1 }, mcp = true }), default);
            await Assert.ThrowsAsync<InvalidDataException>(() => execution.ExecuteAsync(R("config.set", new { allowOnline = true, mcp = true }), default));
            await execution.ExecuteAsync(R("presets.save", new { name = "p", parameters = new { rate = 1.2 } }), default);
            await execution.ExecuteAsync(R("presets.delete", new { name = "p", mcp = true }), default);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task NewGuiReadingReplacesLateAgentAudioAndStaleStopLeavesNewPlayer()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var pending = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        var speech = new Speech(_ => { if (Interlocked.Increment(ref calls) == 1) { started.TrySetResult(); return pending.Task; } return Task.FromResult(Wave()); });
        var outputs = new Outputs(); using var reading = new ReadingSession(speech, outputs);
        using var execution = new AgentExecution(reading, speech, () => new(), action => action(), directory);
        try
        {
            var job = (AgentJob)await execution.ExecuteAsync(R("speak", new { text = "agent fixture" }), default); await started.Task;
            var gui = reading.ReadAsync(_ => Task.FromResult("GUI fixture"), new()); pending.SetResult(Wave());
            await gui; await execution.WaitForJobAsync(job.Id, default);
            Assert.Equal("cancelled", (await Status(execution, job.Id)).State); Assert.Equal(ReadingState.Playing, reading.Status.State);
            var player = Assert.Single(outputs.Players); await execution.ExecuteAsync(R("jobs.stop", new { id = job.Id }), default);
            Assert.False(player.Stopped); Assert.Equal(ReadingState.Playing, reading.Status.State); reading.Stop(); Assert.True(player.Stopped);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task GuiStopCancelsLateAgentSynthesis()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new Speech(_ => { started.TrySetResult(); return pending.Task; }); using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(), action => action(), directory);
        try
        {
            var job = (AgentJob)await execution.ExecuteAsync(R("speak", new { text = "fixture" }), default); await started.Task;
            // ReadingSession cancels before any later audio can reach the factory.
            reading.Stop(); pending.SetResult(Wave()); await execution.WaitForJobAsync(job.Id, default);
            Assert.Equal("cancelled", (await Status(execution, job.Id)).State);
        }
        finally { Directory.Delete(directory, true); }
    }
}
