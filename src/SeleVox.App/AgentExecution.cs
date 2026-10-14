using System.Text.Json;
using SeleVox.Core;

namespace SeleVox.App;
internal sealed record AgentJob(string Id, string State, string? Error = null, string? Path = null, long? Frames = null, double? Seconds = null, string? Format = null);
internal sealed class AgentExecution : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, AgentJob> jobs = new();
    private readonly Queue<string> history = new();
    private readonly AudioGeneration generator;
    private readonly ReadingSession reading;
    private readonly Func<Settings> getSettings;
    private readonly Func<Func<Task>, Task> onUi;
    private readonly Action? beginReading;
    private readonly AgentStore store;
    private readonly CloneLibrary clones;
    private CancellationTokenSource? active;
    private string? activeId;
    private bool disposed;
    internal AgentExecution(ReadingSession reading, ISpeechSynthesis speech, Func<Settings> getSettings, Func<Func<Task>, Task> onUi, string directory, Action? beginReading = null)
    {
        this.reading = reading; generator = new(speech); this.getSettings = getSettings; this.onUi = onUi; this.beginReading = beginReading;
        store = new(Path.Combine(directory, "agent.json")); clones = new(Path.Combine(directory, "clone-samples"));
    }
    internal async Task<object> ExecuteAsync(AgentRequest request, CancellationToken connection)
    {
        if (request.Version != 1 || request.Arguments.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var a = request.Arguments;
        string? Str(string n) => a.TryGetProperty(n, out var v) ? v.GetString() : null;
        JsonElement Fields() => a.TryGetProperty("parameters", out var v) ? v : AgentJson.Empty;
        void Only(params string[] names) { var seen = new HashSet<string>(); foreach (var p in a.EnumerateObject()) if (!seen.Add(p.Name) || !names.Contains(p.Name)) throw new InvalidDataException(); }
        var config = store.Load();
        Settings main = new();
        await onUi(() => { main = getSettings(); return Task.CompletedTask; });
        switch (request.Command)
        {
            case "providers": Only(); return Enum.GetNames<TtsProvider>();
            case "devices": Only(); return AudioOutputs.GetDevices();
            case "clones": Only(); return clones.Load().Select(s => new { s.Id, s.Name, s.Source, s.Created }).ToArray();
            case "voices":
                Only("provider"); var provider = Str("provider") ?? AgentParameters.Resolve(main, config, null, AgentJson.Empty).Settings.Provider.ToString();
                if (!Enum.TryParse<TtsProvider>(provider, out var p) || !Enum.IsDefined(p)) throw new InvalidDataException();
                return p switch { TtsProvider.Windows => SpeechAudio.GetVoices(), TtsProvider.MiMo => Settings.MiMoVoices,
                    TtsProvider.Edge => await new EdgeSpeech().GetVoicesAsync(connection), TtsProvider.Azure => await new AzureSpeech().GetVoicesAsync(main.AzureRegion, connection),
                    _ => new[] { main.OpenAiVoice } };
            case "presets.list": Only(); return config.Presets.Keys.Order().ToArray();
            case "presets.show": Only("name"); return config.Presets.TryGetValue(Str("name") ?? "", out var preset) ? preset : throw new InvalidDataException();
            case "presets.save":
                Only("name", "parameters"); var name = Str("name") ?? ""; AgentStore.ValidateName(name); var fields = Fields(); AgentParameters.Validate(fields);
                store.Update(c => { c.Presets[name] = fields.Clone(); return c; }); return new { saved = true, name };
            case "presets.delete":
                Only("name", "mcp"); if (IsMcp(a) && !config.ExposeDeletePreset) throw new UnauthorizedAccessException();
                store.Update(c => { if (!c.Presets.Remove(Str("name") ?? "")) throw new InvalidDataException(); return c; }); return new { deleted = true };
            case "config.show": Only(); return config;
            case "config.set":
                Only("parameters", "exportDirectory", "exposeDeletePreset", "exposeSetDefaults", "mcp");
                if (IsMcp(a))
                {
                    if (!config.ExposeSetDefaults || a.EnumerateObject().Any(x => x.Name is not ("parameters" or "mcp"))) throw new UnauthorizedAccessException();
                }
                var updated = store.Update(c => c with {
                    Defaults = AgentParameters.Merge(c.Defaults, Fields()),
                    ExportDirectory = Str("exportDirectory") ?? c.ExportDirectory,
                    ExposeDeletePreset = a.TryGetProperty("exposeDeletePreset", out var del) ? del.GetBoolean() : c.ExposeDeletePreset,
                    ExposeSetDefaults = a.TryGetProperty("exposeSetDefaults", out var def) ? def.GetBoolean() : c.ExposeSetDefaults });
                return updated;
            case "jobs.status": Only("id"); lock (sync) return jobs.TryGetValue(Str("id") ?? "", out var job) ? job : throw new InvalidDataException();
            case "jobs.stop":
                Only("id"); CancellationTokenSource? cancelled; AgentJob stopped;
                lock (sync) { var id = Str("id"); if (id is null || !jobs.TryGetValue(id, out stopped!)) throw new InvalidDataException(); cancelled = id == activeId ? active : null; }
                SafeCancel(cancelled); return stopped;
            case "speak": case "synthesize":
                Only("text", "parameters", "preset", "output", "overwrite", "mcp", "wait");
                var text = Str("text"); if (string.IsNullOrWhiteSpace(text) || text.Length > HttpSpeech.MaxTextLength) throw new InvalidDataException();
                var resolved = AgentParameters.Resolve(main, config, Str("preset"), Fields()); var settings = resolved.Settings;
                if (settings.Provider == TtsProvider.MiMo) { MiMoSpeech.GetStyle(settings); MiMoSpeech.SplitCreativeText(text, settings); }
                var output = request.Command == "synthesize" ? AgentExport.Resolve(Str("output"), config.ExportDirectory, IsMcp(a)) : null;
                var overwrite = a.TryGetProperty("overwrite", out var over) && over.GetBoolean();
                if (output is not null && File.Exists(output) && !overwrite) throw new IOException();
                if (request.Command == "speak")
                {
                    var devices = AudioOutputs.GetDevices().Select(d => d.Id).ToHashSet();
                    if (settings.StrictOutputDevices && (settings.OutputDeviceIds ?? []).Any(d => !devices.Contains(d))) throw new AgentDeviceUnavailableException();
                }
                connection.ThrowIfCancellationRequested();
                var wait = a.TryGetProperty("wait", out var w) && w.GetBoolean();
                var source = wait || IsMcp(a) ? CancellationTokenSource.CreateLinkedTokenSource(connection) : new CancellationTokenSource();
                source.CancelAfter(TimeSpan.FromMinutes(20));
                string jobId;
                lock (sync)
                {
                    if (disposed) { source.Dispose(); throw new OperationCanceledException(); }
                    if (active is not null) { source.Dispose(); throw new AgentBusyException(); }
                    // Keep bounded metadata only; no original text, style or reference audio.
                    while (jobs.Count >= 128) jobs.Remove(history.Dequeue());
                    jobId = Guid.NewGuid().ToString("N"); active = source; activeId = jobId; jobs[jobId] = new(jobId, "queued");
                    history.Enqueue(jobId);
                }
                var run = RunAsync(jobId, text, settings, resolved.CloneId, output, overwrite, IsMcp(a), config.ExportDirectory, source);
                if (wait) await run;
                lock (sync) return jobs[jobId];
            default: throw new InvalidDataException();
        }
    }
    private static bool IsMcp(JsonElement a) => a.TryGetProperty("mcp", out var m) && m.GetBoolean();
    private async Task RunAsync(string id, string text, Settings settings, string? cloneId, string? output, bool overwrite, bool mcp, string root, CancellationTokenSource source)
    {
        try
        {
            var token = source.Token;
            async Task<Stream> Generate(CancellationToken cancellation)
            {
                Set(id, "synthesizing");
                if (settings.Provider == TtsProvider.MiMo && settings.MiMoModel == "mimo-v2.5-tts-voiceclone")
                {
                    if (!Guid.TryParse(cloneId, out var sampleId)) throw new InvalidDataException();
                    var sample = clones.Load().FirstOrDefault(s => s.Id == sampleId) ?? throw new InvalidDataException();
                    var audio = await CloneAudio.ReadAsync(sample.FilePath, cancellation); settings = settings with { MiMoReferenceAudio = audio.DataUri };
                }
                var generated = await generator.GenerateAsync(text, settings, cancellation); cancellation.ThrowIfCancellationRequested();
                using (var stream = generated.Open()) using (var wave = new NAudio.Wave.WaveFileReader(stream))
                { lock (sync) jobs[id] = jobs[id] with { Frames = generated.SampleFrames, Seconds = wave.TotalTime.TotalSeconds, Format = wave.WaveFormat.ToString() }; }
                if (output is not null)
                {
                    using var protection = mcp ? AgentExport.Protect(output, root) : null;
                    await generated.SaveAsync(output, overwrite, cancellation); lock (sync) jobs[id] = jobs[id] with { Path = output };
                }
                return generated.Open();
            }
            if (output is null)
                await onUi(() => { token.ThrowIfCancellationRequested(); beginReading?.Invoke(); return reading.ReadGeneratedAsync(Generate, settings, token); });
            else { using var stream = await Generate(token); }
            token.ThrowIfCancellationRequested(); Set(id, "completed");
        }
        catch (OperationCanceledException) { Set(id, "cancelled"); }
        catch (Exception error) { Set(id, "failed", SafeFailure(error)); }
        finally { lock (sync) { if (activeId == id) { active = null; activeId = null; } source.Dispose(); } }
    }
    private static string SafeFailure(Exception error)
    {
        // Match fixed provider messages; never return arbitrary exception text or HTTP bodies.
        if (error is UserFeedbackException && error.Message is
            "请先保存 MiMo API Key（兼容服务 Key 按服务地址独立保存）。" or
            "请先保存 OpenAI-Compatible API Key（兼容服务 Key 按服务地址独立保存）。" or
            "请先在设置中保存 Azure Speech Key。")
            return "missing_credentials:请先在 SeleVox 主界面保存此服务的凭据。";
        if (error is UserFeedbackException && error.Message is
            "请查询并选择 Azure 区域可用的语音，再保存设置。" or
            "请填写 Azure Speech Region 标识，例如 eastasia。" or
            "tts-1 / tts-1-hd 不支持 Instructions，请清空后保存或选择支持的模型。")
            return "invalid_configuration:请在 SeleVox 主界面检查此服务的区域、模型、音色和 Instructions 设置。";
        return "execution_failed:" + error.GetType().Name;
    }
    internal void PlaybackChanged(Status status)
    { lock (sync) { if (activeId is { } id && jobs[id].State == "synthesizing" && status.State == ReadingState.Playing) jobs[id] = jobs[id] with { State = "playing" }; } }
    private void Set(string id, string state, string? error = null) { lock (sync) jobs[id] = jobs[id] with { State = state, Error = error }; }
    internal async Task WaitForJobAsync(string id, CancellationToken token)
    {
        while (true)
        {
            lock (sync) { if (activeId != id) return; }
            await Task.Delay(100, token);
        }
    }
    private static void SafeCancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }
    internal void CancelJob(string id) { CancellationTokenSource? source; lock (sync) source = activeId == id ? active : null; SafeCancel(source); }
    internal void CancelAll() { CancellationTokenSource? source; lock (sync) source = active; SafeCancel(source); }
    public void Dispose() { CancellationTokenSource? source; lock (sync) { disposed = true; source = active; } SafeCancel(source); }
}
internal sealed class AgentBusyException : Exception;
internal sealed class AgentDeviceUnavailableException : Exception;
internal static class AgentExport
{
    internal static IDisposable Protect(string path, string root)
    {
        var handles = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        try
        {
            Resolve(path, root, true);
            for (string? part = Path.GetDirectoryName(path); part is not null; part = Path.GetDirectoryName(part))
            {
                var handle = AgentExportProtection.OpenDirectory(part, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new UnauthorizedAccessException(); }
                handles.Add(handle);
            }
            Resolve(path, root, true);
            return new DirectoryLease(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class DirectoryLease(List<Microsoft.Win32.SafeHandles.SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }

    internal static string Resolve(string? output, string root, bool restricted)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException();
        root = Path.GetFullPath(root);
        var boundary = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.IsPathFullyQualified(output) ? output : Path.Combine(root, output));
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) || path.IndexOf(':', 2) >= 0) throw new InvalidDataException();
        if (restricted && !path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        if (restricted)
        {
            // Reject every existing ancestor, including ancestors of the configured root, and the destination itself.
            for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path;
    }
}
