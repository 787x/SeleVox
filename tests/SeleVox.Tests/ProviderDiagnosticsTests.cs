using System.Net;
using System.Text;
using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class ProviderDiagnosticsTests
{
    internal static DiagnosticSnapshot Snapshot(DiagnosticService service, Settings? settings = null, string? key = "fixture") =>
        DiagnosticSnapshot.Create(service, settings ?? new(VoiceId: "local", AzureRegion: "eastasia", AzureVoice: "zh-CN-XiaoxiaoNeural", EdgeVoice: "zh-CN-XiaoxiaoNeural", LlmModel: "fixture", MiMoStreaming: false), key);
    internal static ProviderDiagnostics Fake(Func<DiagnosticSnapshot, string, CancellationToken, Task<Stream>>? synth = null,
        Func<DiagnosticSnapshot, StreamingPcmBuffer, CancellationToken, Task>? stream = null,
        Func<DiagnosticSnapshot, CancellationToken, Task<string>>? answer = null, TimeSpan? timeout = null) =>
        new(synthesize: synth ?? ((_, _, _) => Task.FromResult<Stream>(new MemoryStream(AudioGenerationTests.Wave()))),
            stream: stream, answer: answer ?? ((_, _) => Task.FromResult("OK")), voices: () => [new("local", "Local")], timeout: timeout);
    [Theory]
    [InlineData(DiagnosticService.Windows)] [InlineData(DiagnosticService.Azure)] [InlineData(DiagnosticService.Edge)]
    [InlineData(DiagnosticService.MiMo)] [InlineData(DiagnosticService.OpenAiTts)] [InlineData(DiagnosticService.Llm)]
    internal async Task PrecheckDoesNotCallAndOnlyFullResponseSucceeds(DiagnosticService service)
    {
        var calls = 0; var snapshot = Snapshot(service);
        var test = Fake((s, text, _) => { ++calls; Assert.Equal(ProviderDiagnostics.TestText, text); Assert.Equal(snapshot.Identity, s.Identity); return Task.FromResult<Stream>(new MemoryStream(AudioGenerationTests.Wave())); },
            answer: (s, _) => { ++calls; Assert.Equal(ProviderDiagnostics.SystemPrompt, s.RequestSettings.LlmSystemPrompt); return Task.FromResult("OK"); });
        test.Precheck(snapshot); Assert.Equal(0, calls);
        var result = await test.RunAsync(snapshot, default);
        Assert.Equal(DiagnosticState.Succeeded, result.State); Assert.Equal(1, calls); Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Equal(TtsProvider.Windows, snapshot.Settings.Provider); // Daily provider was never changed.
    }
    [Theory]
    [InlineData(DiagnosticService.Windows)] [InlineData(DiagnosticService.Azure)] [InlineData(DiagnosticService.Edge)]
    [InlineData(DiagnosticService.MiMo)] [InlineData(DiagnosticService.OpenAiTts)] [InlineData(DiagnosticService.Llm)]
    internal async Task EmptyOrTruncatedResponseAndTimeoutFail(DiagnosticService service)
    {
        var snapshot = Snapshot(service);
        foreach (var bytes in new[] { Array.Empty<byte>(), AudioGenerationTests.Wave()[..^2] })
        {
            var invalid = Fake((_, _, _) => Task.FromResult<Stream>(new MemoryStream(bytes)), answer: (_, _) => Task.FromResult(" "));
            Assert.Equal(DiagnosticState.Failed, (await invalid.RunAsync(snapshot, default)).State);
        }
        var timeout = Fake(async (_, _, token) => { await Task.Delay(-1, token); throw new Exception(); },
            answer: async (_, token) => { await Task.Delay(-1, token); return ""; }, timeout: TimeSpan.FromMilliseconds(40));
        Assert.Equal(ServiceFailure.Timeout, (await timeout.RunAsync(snapshot, default)).Failure);
    }
    [Theory] [InlineData(DiagnosticService.Azure)] [InlineData(DiagnosticService.MiMo)] [InlineData(DiagnosticService.OpenAiTts)] [InlineData(DiagnosticService.Llm)]
    internal async Task MissingKeyDoesNotSubmit(DiagnosticService service)
    {
        var calls = 0; var diagnostics = Fake((_, _, _) => { ++calls; throw new Exception(); });
        var result = await diagnostics.RunAsync(Snapshot(service, key: null), default);
        Assert.Equal(ServiceFailure.MissingCredential, result.Failure); Assert.Equal(0, calls);
    }
    [Fact] public async Task NoLocalVoicesFailsBeforeSynthesis()
    {
        var service = new ProviderDiagnostics(voices: () => []);
        Assert.Equal(ServiceFailure.Configuration, (await service.RunAsync(Snapshot(DiagnosticService.Windows), default)).Failure);
    }
    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(400)] [InlineData(422)] [InlineData(503)]
    public async Task ActualClientsExposeStructuredSafeHttpFailuresWithOneRequest(int status)
    {
        foreach (var service in new[] { DiagnosticService.Azure, DiagnosticService.MiMo, DiagnosticService.OpenAiTts, DiagnosticService.Llm })
        {
            var calls = 0;
            using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => { ++calls; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret-response") }); }));
            var diag = Fake((s, text, token) => service switch {
                DiagnosticService.Azure => new AzureSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, token),
                DiagnosticService.MiMo => new MiMoSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, token),
                _ => new OpenAiSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, token)
            }, answer: (s, t) => new ChatCompletionClient(http, requireConfiguredMode: true).GenerateAsync(ProviderDiagnostics.Question, s.RequestSettings, s.Key!, _ => { }, t));
            var result = await diag.RunAsync(Snapshot(service), default);
            Assert.Equal(DiagnosticState.Failed, result.State); Assert.Equal(status, result.HttpStatus); Assert.Equal(UserFeedbackException.Http(status).Failure, result.Failure);
            Assert.DoesNotContain("secret", result.Message); Assert.Equal(1, calls);
        }
        var edge = EdgeSpeech.Feedback(new HttpRequestException("secret", null, (HttpStatusCode)status));
        Assert.Equal(status, edge.HttpStatus); Assert.DoesNotContain("secret", edge.Message);
    }
    [Theory] [InlineData("mimo-v2.5-tts")] [InlineData("mimo-v2.5-tts-voicedesign")]
    public async Task ActualWaveClientsAndDesignSendOneFixedInput(string model)
    {
        foreach (var service in new[] { DiagnosticService.Azure, DiagnosticService.MiMo, DiagnosticService.OpenAiTts })
        {
            var calls = 0;
            using var http = new HttpClient(new MiMoStreamingTests.Handler(async (r, t) => {
                ++calls; var body = await r.Content!.ReadAsStringAsync(t); if (service == DiagnosticService.Azure) Assert.Contains(ProviderDiagnostics.TestText, body); else { using var input = JsonDocument.Parse(body); Assert.Equal(ProviderDiagnostics.TestText, service == DiagnosticService.MiMo ? input.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() : input.RootElement.GetProperty("input").GetString()); }
                Assert.DoesNotContain("/models", r.RequestUri!.AbsolutePath);
                if (service == DiagnosticService.MiMo)
                {
                    using var json = JsonDocument.Parse(body); Assert.Equal(model, json.RootElement.GetProperty("model").GetString());
                    if (model.EndsWith("voicedesign")) Assert.False(json.RootElement.GetProperty("audio").TryGetProperty("voice", out _));
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(AudioGenerationTests.Wave()) } } } } })) };
                }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(AudioGenerationTests.Wave()) };
            }));
            var diag = Fake((s, text, t) => service switch { DiagnosticService.Azure => new AzureSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, t),
                DiagnosticService.MiMo => new MiMoSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, t), _ => new OpenAiSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, t) });
            var snapshot = Snapshot(service, Snapshot(service).Settings with { MiMoModel = model });
            var result = await diag.RunAsync(snapshot, default); Assert.True(result.State == DiagnosticState.Succeeded, service + ": " + result.Message); Assert.Equal(1, calls);
        }
    }
    [Theory] [InlineData("complete", true)] [InlineData("truncated", false)] [InlineData("empty", false)] [InlineData("odd", false)] [InlineData("error", false)]
    public async Task MiMoStreamingDrainsBeyondRingAndRequiresValidCompletion(string kind, bool success)
    {
        var body = kind == "empty" ? "" : MiMoStreamingTests.Event(new byte[kind == "odd" ? 3 : StreamingPcmBuffer.BytesPerSecond * 8]);
        if (kind == "error") body += "data: {\"error\":\"private\"}\n\n";
        if (kind != "truncated") body += "data: [DONE]\n\n";
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(new MemoryStream(Encoding.UTF8.GetBytes(body))))));
        var diag = Fake(stream: (s, b, t) => new MiMoSpeech(http, () => s.Key).SynthesizeStreamingAsync(ProviderDiagnostics.TestText, s.RequestSettings, b, t));
        var snapshot = Snapshot(DiagnosticService.MiMo, new(MiMoStreaming: true)); Assert.Contains("SSE PCM", snapshot.Summary);
        var result = await diag.RunAsync(snapshot, default); Assert.Equal(success ? DiagnosticState.Succeeded : DiagnosticState.Failed, result.State); Assert.DoesNotContain("private", result.Message);
    }
    [Fact] public async Task CloneNeverUsesSavedSampleAndModelsDoNotShareIdentity()
    {
        var source = new Settings(MiMoModel: "mimo-v2.5-tts-voiceclone") { MiMoReferenceAudio = "private-sample" };
        var s = Snapshot(DiagnosticService.MiMo, source); Assert.Null(s.RequestSettings.MiMoReferenceAudio);
        var result = await Fake().RunAsync(s, default); Assert.Equal(ServiceFailure.Configuration, result.Failure); Assert.Contains("确认上传", result.Message);
        Assert.NotEqual(s.Identity, Snapshot(DiagnosticService.MiMo, source with { MiMoModel = "mimo-v2.5-tts" }).Identity);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LlmRealParserUsesConfiguredModeThinkingAndFixedPrompt(bool streaming)
    {
        var requestCount = 0;
        using var http = new HttpClient(new MiMoStreamingTests.Handler(async (r, t) => {
            ++requestCount; using var j = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(t)); var root = j.RootElement;
            Assert.Equal("high", root.GetProperty("reasoning_effort").GetString()); Assert.Equal(streaming, root.GetProperty("stream").GetBoolean());
            Assert.Equal(ProviderDiagnostics.SystemPrompt, root.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal(ProviderDiagnostics.Question, root.GetProperty("messages")[1].GetProperty("content").GetString());
            return streaming ? LlmResponse("data: {\"choices\":[{\"delta\":{\"content\":\"OK\",\"reasoning_content\":\"secret\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", true)
                : LlmResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}", false);
        }));
        var diag = Fake(answer: (s, t) => new ChatCompletionClient(http, requireConfiguredMode: true).GenerateAsync(ProviderDiagnostics.Question, s.RequestSettings, s.Key!, _ => { }, t));
        var snapshot = Snapshot(DiagnosticService.Llm, new(LlmModel: "fixture", LlmSystemPrompt: "private-prompt", LlmStreaming: streaming, LlmThinkingProtocol: ThinkingProtocol.OpenAi, LlmThinking: ThinkingOption.High));
        var result = await diag.RunAsync(snapshot, default); Assert.Equal(DiagnosticState.Succeeded, result.State); Assert.Equal(1, requestCount);
        Assert.Equal("private-prompt", snapshot.Settings.LlmSystemPrompt); Assert.DoesNotContain("secret", result.Message);
    }
    private static HttpResponseMessage LlmResponse(string body, bool streaming) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"private\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("data: {\"error\":\"private\"}\n\n")]
    public async Task LlmPartialReasoningAndBadFinishAreNotSuccess(string body)
    {
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(LlmResponse(body, true))));
        var diag = Fake(answer: (s, t) => new ChatCompletionClient(http, requireConfiguredMode: true).GenerateAsync(ProviderDiagnostics.Question, s.RequestSettings, s.Key!, _ => { }, t));
        Assert.Equal(DiagnosticState.Failed, (await diag.RunAsync(Snapshot(DiagnosticService.Llm), default)).State);
    }
    [Fact] public async Task StreamingCannotBeCertifiedByJsonFallback()
    {
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(LlmResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}", false))));
        var diag = Fake(answer: (s, t) => new ChatCompletionClient(http, requireConfiguredMode: true).GenerateAsync(ProviderDiagnostics.Question, s.RequestSettings, s.Key!, _ => { }, t));
        Assert.Equal(DiagnosticState.Failed, (await diag.RunAsync(Snapshot(DiagnosticService.Llm), default)).State);
    }
    [Fact] public async Task ConcurrentClickIsRejectedAndLateCanceledResultCannotSucceed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var diag = Fake(async (_, _, _) => { ++calls; entered.TrySetResult(); await release.Task; return new MemoryStream(AudioGenerationTests.Wave()); });
        using var cancel = new CancellationTokenSource(); var pending = diag.RunAsync(Snapshot(DiagnosticService.Windows), cancel.Token); await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => diag.RunAsync(Snapshot(DiagnosticService.Azure), default)); cancel.Cancel(); release.TrySetResult();
        Assert.Equal(DiagnosticState.Canceled, (await pending).State); Assert.Equal(1, calls);
        Assert.Equal(DiagnosticState.Succeeded, (await diag.RunAsync(Snapshot(DiagnosticService.Windows), default)).State);
    }
    [Fact] public void IdentityCopiesArraysAndChangesWithSettingsKeyAndAddress()
    {
        var styles = new[] { "温柔" }; var settings = new Settings(MiMoOverallStyles: styles); var snapshot = Snapshot(DiagnosticService.MiMo, settings);
        styles[0] = "清亮"; Assert.Equal("温柔", snapshot.Settings.MiMoOverallStyles![0]);
        Assert.NotEqual(snapshot.Identity, Snapshot(DiagnosticService.MiMo, settings).Identity);
        Assert.NotEqual(snapshot.Identity, Snapshot(DiagnosticService.MiMo, snapshot.Settings, "new-key").Identity);
        Assert.NotEqual(snapshot.Identity, Snapshot(DiagnosticService.MiMo, snapshot.Settings with { OpenAiBaseUrl = "https://example.com/v1" }).Identity);
        Assert.DoesNotContain("private-path", Snapshot(DiagnosticService.OpenAiTts, new(OpenAiBaseUrl: "https://example.com/private-path")).Summary);
    }
    [Fact] public async Task RealWindowsDiagnosticSynthesizesWithoutPlaybackDevice()
    {
        var voices = SpeechAudio.GetVoices(); Assert.NotEmpty(voices);
        var snapshot = Snapshot(DiagnosticService.Windows, new(VoiceId: voices[0].Id, OutputDeviceIds: ["nonexistent-device"]));
        var result = await new ProviderDiagnostics().RunAsync(snapshot, default); Assert.True(result.State == DiagnosticState.Succeeded, result.Message);
    }
    [Fact] public async Task AzureRejectsOversizedBodyBeforeReading()
    {
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = HttpSpeech.MaxResponseBytes + 1L; return Task.FromResult(response);
        }));
        var diag = Fake((s, text, t) => new AzureSpeech(http, () => s.Key).SynthesizeAsync(text, s.RequestSettings, t));
        Assert.Equal(ServiceFailure.ResponseTooLarge, (await diag.RunAsync(Snapshot(DiagnosticService.Azure), default)).Failure);
    }
    [Theory] [InlineData(DiagnosticService.Windows)] [InlineData(DiagnosticService.Azure)] [InlineData(DiagnosticService.Edge)]
    [InlineData(DiagnosticService.MiMo)] [InlineData(DiagnosticService.OpenAiTts)] [InlineData(DiagnosticService.Llm)]
    internal async Task ConnectionFailuresAreSafeAndDoNotSwitchProvider(DiagnosticService service)
    {
        var calls = 0;
        var diag = Fake((_, _, _) => { ++calls; throw new HttpRequestException("private-url-and-key"); },
            answer: (_, _) => { ++calls; throw new HttpRequestException("private-url-and-key"); });
        var result = await diag.RunAsync(Snapshot(service), default);
        Assert.Equal(ServiceFailure.Connection, result.Failure); Assert.DoesNotContain("private", result.Message); Assert.Equal(1, calls);
    }
    [Fact] public void ActualCredentialManagerKeepsAddressAndServiceNamespacesSeparate()
    {
        var address = "http://127.0.0.1/pr15-" + Guid.NewGuid().ToString("N"); var other = address + "-other";
        var tts = AzureCredentials.OpenAi(address); var llm = AzureCredentials.Llm(address); var second = AzureCredentials.OpenAi(other);
        try
        {
            tts.Write("synthetic-key"); Assert.Equal("synthetic-key", ProviderDiagnostics.ReadKey(DiagnosticService.OpenAiTts, new(OpenAiBaseUrl: address)));
            Assert.Null(ProviderDiagnostics.ReadKey(DiagnosticService.OpenAiTts, new(OpenAiBaseUrl: other)));
            Assert.Null(ProviderDiagnostics.ReadKey(DiagnosticService.Llm, new(LlmBaseUrl: address)));
            llm.Write("synthetic-llm-key"); Assert.Equal("synthetic-llm-key", llm.Read()); Assert.Equal("synthetic-key", tts.Read());
        }
        finally { tts.Clear(); llm.Clear(); second.Clear(); }
    }
}
