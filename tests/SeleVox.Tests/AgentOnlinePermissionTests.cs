using System.Net;
using System.Text.Json;
using SeleVox.App;
using SeleVox.Cli;
using SeleVox.Core;
namespace SeleVox.Tests;
public sealed class AgentOnlinePermissionTests
{
    private sealed class Speech(Func<string, Settings, CancellationToken, Task<Stream>> synthesize) : ISpeechSynthesis
    { public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation) => synthesize(text, settings, cancellation); }
    private sealed class NoPlayback : IAudioPlaybackFactory
    { public IAudioPlayback CreatePlayback(Stream audio, Settings settings) => throw new InvalidOperationException(); }
    private sealed class Handler(TtsProvider provider) : HttpMessageHandler
    {
        public List<(Uri? Address, string? Key)> Calls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add((request.RequestUri, request.Headers.Authorization?.Parameter));
            using var wave = MultiOutputTests.Fixture();
            HttpContent content = provider == TtsProvider.MiMo
                ? new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(wave.ToArray()) } } } } }))
                : new ByteArrayContent(wave.ToArray());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private static void Legacy(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "agent.json"), JsonSerializer.Serialize(new {
            AllowOnline = false, AuthorizedOpenAiAddress = "https://retired.example/v1",
            Defaults = new { rate = 1.1 }, Presets = new { retained = new { volume = .4 } }, ExportDirectory = directory }));
    }
    [Theory] [InlineData(TtsProvider.MiMo)] [InlineData(TtsProvider.OpenAiCompatible)]
    public async Task CliAndMcpUseSavedServiceDespiteLegacyDeniedPermission(TtsProvider provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); Legacy(directory);
        using var handler = new Handler(provider); using var http = new HttpClient(handler);
        var main = new Settings(Provider: provider, OpenAiBaseUrl: "https://saved.example/v1");
        var speech = new Speech((text, settings, token) => provider == TtsProvider.MiMo
            ? new MiMoSpeech(http, () => "fixture-MiMo").SynthesizeAsync(text, settings, token)
            : new OpenAiSpeech(http, () => "fixture-saved-service").SynthesizeAsync(text, settings, token));
        using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => main, action => action(), directory);
        var name = "SeleVox-Test-" + Guid.NewGuid().ToString("N"); using var pipe = new AgentPipe(execution, name); using var client = new AgentClient(name);
        try
        {
            var cli = await CliArguments.ParseAsync(["synthesize", "--text", "fixture", "--output", Path.Combine(directory, "cli.wav"), "--wait"], default);
            var first = await client.CallAsync(cli.Command, cli.Arguments); Assert.True(first.Success); Assert.Equal("completed", first.Data.GetProperty("State").GetString());
            var tool = await new AgentTools(client).SynthesizeAudio("fixture", "mcp.wav"); Assert.False(tool.IsError);
            var id = tool.StructuredContent!.Value.GetProperty("Data").GetProperty("Id").GetString()!;
            await execution.WaitForJobAsync(id, new CancellationTokenSource(5000).Token);
            var status = await client.CallAsync("jobs.status", new { id }); Assert.Equal("completed", status.Data.GetProperty("State").GetString());
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "cli.wav")), File.ReadAllBytes(Path.Combine(directory, "mcp.wav")));
            Assert.Equal(2, handler.Calls.Count);
            Assert.All(handler.Calls, call => {
                Assert.Equal(provider == TtsProvider.MiMo ? "https://api.xiaomimimo.com/v1/chat/completions" : "https://saved.example/v1/audio/speech", call.Address!.AbsoluteUri);
                Assert.Equal(provider == TtsProvider.MiMo ? "fixture-MiMo" : "fixture-saved-service", call.Key);
            });
            var denied = await client.CallAsync("synthesize", new { text = "fixture", output = "blocked.wav", parameters = new { OpenAiBaseUrl = "https://other.example/v1" } });
            Assert.False(denied.Success); Assert.Equal(2, handler.Calls.Count);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task LegacyFieldsAreIgnoredAndRemovedOnSaveButUnknownFieldsStillFail()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); Legacy(directory);
        var path = Path.Combine(directory, "agent.json"); var store = new AgentStore(path);
        try
        {
            Assert.Equal(1.1, store.Load().Defaults.GetProperty("rate").GetDouble()); Assert.True(store.Load().Presets.ContainsKey("retained"));
            store.Update(c => c with { ExposeSetDefaults = true }); var json = File.ReadAllText(path);
            Assert.DoesNotContain("AllowOnline", json); Assert.DoesNotContain("AuthorizedOpenAiAddress", json); Assert.True(store.Load().Presets.ContainsKey("retained"));
            File.WriteAllText(path, "{\"AllowOnline\":false,\"AuthorizedOpenAiAddress\":\"not a URL\"}"); store.Load();
            File.WriteAllText(path, "{\"apiKey\":\"private-sentinel\"}"); Assert.Throws<JsonException>(() => store.Load());
            await Assert.ThrowsAsync<InvalidDataException>(() => CliArguments.ParseAsync(["config", "set", "--allow-online", "true"], default));
            Assert.DoesNotContain("--allow-online", CliArguments.Help);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task MissingAzureConfigurationIsClearWithoutSendingRequests(bool hasVoice)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var handler = new Handler(TtsProvider.Azure); using var http = new HttpClient(handler);
        var speech = new Speech((text, settings, token) => new AzureSpeech(http, () => "fixture").SynthesizeAsync(text, settings, token));
        using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(Provider: TtsProvider.Azure, AzureRegion: hasVoice ? "" : "eastus", AzureVoice: hasVoice ? "en-US-JennyNeural" : null), action => action(), directory);
        try
        {
            var job = (AgentJob)await execution.ExecuteAsync(new(1, "synthesize", AgentJson.Element(new { text = "fixture", output = Path.Combine(directory, "out.wav"), wait = true })), default);
            Assert.Equal("failed", job.State); Assert.StartsWith("invalid_configuration:", job.Error); Assert.Empty(handler.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory] [InlineData(TtsProvider.Azure)] [InlineData(TtsProvider.MiMo)] [InlineData(TtsProvider.OpenAiCompatible)]
    public async Task MissingCredentialsGiveSafeActionableJobError(TtsProvider provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var handler = new Handler(provider); using var http = new HttpClient(handler);
        var speech = new Speech((text, settings, token) => provider switch {
            TtsProvider.Azure => new AzureSpeech(http, () => null).SynthesizeAsync(text, settings, token),
            TtsProvider.MiMo => new MiMoSpeech(http, () => null).SynthesizeAsync(text, settings, token),
            _ => new OpenAiSpeech(http, () => null).SynthesizeAsync(text, settings, token) });
        using var reading = new ReadingSession(speech, new NoPlayback());
        using var execution = new AgentExecution(reading, speech, () => new(Provider: provider, AzureRegion: "eastus", AzureVoice: "en-US-JennyNeural"), action => action(), directory);
        try
        {
            var job = (AgentJob)await execution.ExecuteAsync(new(1, "synthesize", AgentJson.Element(new { text = "private-sentinel", output = Path.Combine(directory, "out.wav"), wait = true })), default);
            Assert.Equal("failed", job.State); Assert.StartsWith("missing_credentials:", job.Error); Assert.Contains("主界面", job.Error); Assert.DoesNotContain("private-sentinel", job.Error); Assert.Empty(handler.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }
}
