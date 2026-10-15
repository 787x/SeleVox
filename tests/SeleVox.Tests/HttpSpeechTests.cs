using System.Net;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class HttpSpeechTests
{
    private static byte[] Wave(int rate = 24000, byte value = 1)
    {
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(rate, 16, 1)))
            writer.Write(Enumerable.Repeat(value, 480).ToArray(), 0, 480);
        return memory.ToArray();
    }
    private static HttpResponseMessage Response(bool mimo, byte[]? wave = null) => new(HttpStatusCode.OK)
    {
        Content = mimo ? new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(wave ?? Wave()) } } } } })) : new ByteArrayContent(wave ?? Wave())
    };
    private static Task<Stream> Synthesize(bool mimo, HttpClient client, string text, Settings? settings = null, CancellationToken token = default) =>
        mimo ? new MiMoSpeech(client, () => "fixture-key").SynthesizeAsync(text, settings ?? new(), token) :
        new OpenAiSpeech(client, () => "fixture-key").SynthesizeAsync(text, settings ?? new(), token);

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ProtocolsPreserveTextAndSendOnlySupportedFields(bool mimo)
    {
        const string text = "原文 <private & text> 😀 é\nDo not translate.";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.True(request.Headers.Authorization.Parameter == "fixture-key", "Expected fixture authorization.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = json.RootElement;
            if (mimo)
            {
                Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
                Assert.Equal("mimo-v2.5-tts", root.GetProperty("model").GetString());
                var messages = root.GetProperty("messages"); Assert.Equal("user", messages[0].GetProperty("role").GetString());
                Assert.Contains("calm", messages[0].GetProperty("content").GetString());
                Assert.Contains("放慢", messages[0].GetProperty("content").GetString());
                Assert.Equal("assistant", messages[1].GetProperty("role").GetString()); Assert.Equal(text, messages[1].GetProperty("content").GetString());
                Assert.Equal("冰糖", root.GetProperty("audio").GetProperty("voice").GetString());
                Assert.Equal("wav", root.GetProperty("audio").GetProperty("format").GetString());
                Assert.False(root.TryGetProperty("speed", out _)); Assert.False(root.GetProperty("stream").GetBoolean());
            }
            else
            {
                Assert.Equal("https://api.openai.com/v1/audio/speech", request.RequestUri!.AbsoluteUri);
                Assert.Equal(text, root.GetProperty("input").GetString()); Assert.Equal(.5, root.GetProperty("speed").GetDouble());
                Assert.Equal("gpt-4o-mini-tts", root.GetProperty("model").GetString()); Assert.Equal("coral", root.GetProperty("voice").GetString());
                Assert.Equal("wav", root.GetProperty("response_format").GetString()); Assert.Equal("calm", root.GetProperty("instructions").GetString());
            }
            return Response(mimo);
        }));
        using var audio = await Synthesize(mimo, http, text, new(Rate: .5, MiMoStyle: "calm", OpenAiInstructions: "calm", OpenAiBaseUrl: "https://api.openai.com/v1///"));
        using var wave = new WaveFileReader(audio); Assert.Equal(480, wave.Length);
    }
    [Fact] public void MiMoDefaultOmitsOptionalUserMessage()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MiMoSpeech.CreateRequest("unchanged", "Mia", "", "mimo-v2.5-tts")));
        var message = Assert.Single(json.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("assistant", message.GetProperty("role").GetString());
    }
    [Fact] public async Task MiMoModelIsEditableAndDefaultsRemainCompatible()
    {
        Assert.Equal("mimo-v2.5-tts", new Settings().MiMoModel);
        Assert.Equal("mimo-v2.5-tts", JsonSerializer.Deserialize<Settings>("{\"Provider\":3}")!.MiMoModel);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("next-compatible-model", json.RootElement.GetProperty("model").GetString());
            return Response(true);
        }));
        using var audio = await Synthesize(true, http, "fixture", new(MiMoModel: "next-compatible-model"));
        using var never = new HttpClient(new Handler((_, _) => throw new Exception("must not send")));
        await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(true, never, "fixture", new(MiMoModel: " ")));
        Assert.False(new Settings(MiMoModel: " ").IsValid);
    }
    [Theory] [InlineData("tts-1")] [InlineData("tts-1-hd")]
    public async Task LegacyModelsRejectInstructionsBeforeSending(string model)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("must not send")));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(false, http, "fixture", new(OpenAiModel: model, OpenAiInstructions: "calm")));
        Assert.Contains("不支持", error.Message);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(OpenAiSpeech.CreateRequest("fixture", new(OpenAiModel: model))));
        Assert.False(json.RootElement.TryGetProperty("instructions", out _));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task LongUnicodeTextIsPreservedAndPcmMergedInOrder(bool mimo)
    {
        var text = string.Concat(Enumerable.Repeat("句子 😀 é 👨‍👩‍👧‍👦。\r\n", 700));
        var parts = new List<string>(); var expected = new List<byte>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var part = mimo ? json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()! : json.RootElement.GetProperty("input").GetString()!;
            Assert.True(mimo ? Encoding.UTF8.GetByteCount(part) <= 3000 : part.Length <= 4096);
            parts.Add(part); var value = (byte)parts.Count; expected.AddRange(Enumerable.Repeat(value, 480)); return Response(mimo, Wave(value: value));
        }));
        using var audio = await Synthesize(mimo, http, text); using var wave = new WaveFileReader(audio);
        Assert.True(parts.Count > 1); Assert.Equal(text, string.Concat(parts));
        var actual = new byte[wave.Length]; Assert.Equal(actual.Length, wave.Read(actual, 0, actual.Length)); Assert.Equal(expected.ToArray(), actual);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task InconsistentSegmentFormatsFailWithoutPartialAudio(bool mimo)
    {
        var calls = 0; using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(mimo, Wave(++calls == 1 ? 24000 : 16000)))));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(mimo, http, new string('a', 5000)));
        Assert.Contains("格式不一致", error.Message); Assert.Equal(2, calls);
    }
    [Theory] [InlineData("{}")] [InlineData("invalid-json")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"audio\":{\"data\":\"\"}}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"audio\":{\"data\":\"private!\"}}}]}")]
    public async Task InvalidMiMoResponseGivesSafeFeedback(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(true, http, "fixture")); Assert.DoesNotContain("private", error.Message);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task InvalidAndTruncatedWaveRejected(bool mimo)
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("private"), Wave()[..^1], new byte[44] })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(mimo, bytes))));
            var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(mimo, http, "fixture")); Assert.DoesNotContain("private", error.Message);
        }
    }
    [Fact] public async Task OpenAiUnknownStreamingLengthsAreFinalizedOnlyAfterHttpEof()
    {
        var source = Wave(); BitConverter.TryWriteBytes(source.AsSpan(4, 4), uint.MaxValue); BitConverter.TryWriteBytes(source.AsSpan(40, 4), uint.MaxValue);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(false, source))));
        using var audio = await Synthesize(false, http, "fixture"); using var reader = new WaveFileReader(audio); Assert.Equal(480, reader.Length);
        foreach (var sourceBytes in new[] { Wave(), Wave()[..^1] })
        {
            // A single sentinel paired with an ordinary declared size is malformed.
            BitConverter.TryWriteBytes(sourceBytes.AsSpan(4, 4), uint.MaxValue);
            using var invalid = new HttpClient(new Handler((_, _) => Task.FromResult(Response(false, sourceBytes))));
            await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(false, invalid, "fixture"));
        }
        var unaligned = source[..^1]; BitConverter.TryWriteBytes(unaligned.AsSpan(4, 4), uint.MaxValue); BitConverter.TryWriteBytes(unaligned.AsSpan(40, 4), uint.MaxValue);
        using var partial = new HttpClient(new Handler((_, _) => Task.FromResult(Response(false, unaligned))));
        await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(false, partial, "fixture"));
    }
    [Fact] public void WaveInvalidFormatAndEmptyPcmAreRejected()
    {
        foreach (var offset in new[] { 20, 22, 24, 28, 32, 34 })
        {
            var bytes = Wave(); bytes[offset] = 0; bytes[offset + 1] = 0;
            Assert.Throws<UserFeedbackException>(() => HttpSpeech.ValidatedWave(bytes));
        }
        var empty = Wave()[..44]; BitConverter.TryWriteBytes(empty.AsSpan(4, 4), 36u); BitConverter.TryWriteBytes(empty.AsSpan(40, 4), 0u);
        Assert.Throws<UserFeedbackException>(() => HttpSpeech.ValidatedWave(empty));
    }
    [Fact] public void Base64DecodeSupportsWhitespaceLargeBlocksAndCancellation()
    {
        var bytes = Enumerable.Range(0, 120000).Select(i => (byte)i).ToArray();
        var encoded = Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks);
        Assert.Equal(bytes, MiMoSpeech.DecodeBase64(encoded, default));
        Assert.Throws<FormatException>(() => MiMoSpeech.DecodeBase64("YQ==Yg==", default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MiMoSpeech.DecodeBase64(encoded, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => HttpSpeech.ValidatedWave(Wave(), cancellation.Token));
    }
    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)] [InlineData(400)] [InlineData(302)]
    public async Task HttpFailuresNeverExposeBodyOrFollowRedirect(int code)
    {
        foreach (var mimo in new[] { false, true })
        {
            var calls = 0;
            using var http = new HttpClient(new Handler((_, _) => { ++calls; var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("private key and text") }; response.Headers.Location = new Uri("https://unauthorized.example"); return Task.FromResult(response); }));
            var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(mimo, http, "private"));
            Assert.DoesNotContain("private", error.Message); Assert.Equal(1, calls);
        }
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CancellationAfterFirstSegmentDoesNotReturnPartialAudio(bool mimo)
    {
        using var cancellation = new CancellationTokenSource(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            if (++calls == 1) return Response(mimo);
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Response(mimo);
        }));
        var pending = Synthesize(mimo, http, new string('a', 6000), token: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(2, calls);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task NetworkAndTimeoutErrorsAreSanitized(bool mimo)
    {
        using var failed = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private")));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(mimo, failed, "fixture")); Assert.Contains("网络", error.Message); Assert.DoesNotContain("private", error.Message);
        using var timed = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(mimo); })) { Timeout = TimeSpan.FromMilliseconds(40) };
        error = await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(mimo, timed, "fixture")); Assert.Contains("超时", error.Message);
    }
    [Fact] public async Task BoundedResponseRejectsEvenBeforeReading()
    {
        using var content = new ByteArrayContent([1]); content.Headers.ContentLength = HttpSpeech.MaxResponseBytes + 1;
        await Assert.ThrowsAsync<UserFeedbackException>(() => HttpSpeech.ReadBoundedAsync(content, default));
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("must not send")));
        await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(false, http, new string('a', HttpSpeech.MaxTextLength + 1)));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CancellationDuringBodyReadIsObserved(bool mimo)
    {
        using var cancellation = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new BlockingStream(entered);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        var pending = Synthesize(mimo, http, "fixture", token: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(stream.Disposed);
    }
    [Fact] public async Task StreamingResponseWithoutContentLengthHasBound()
    {
        using var content = new StreamContent(new OversizeStream());
        await Assert.ThrowsAsync<UserFeedbackException>(() => HttpSpeech.ReadBoundedAsync(content, default));
    }
    [Fact] public async Task ProductionHttpHandlerDoesNotFollowCrossHostRedirect()
    {
        // Actual sockets, but only local loopback and a synthetic credential.
        var port = FreePort(); var targetPort = FreePort();
        using var source = new HttpListener(); using var target = new HttpListener();
        source.Prefixes.Add($"http://localhost:{port}/"); target.Prefixes.Add($"http://127.0.0.1:{targetPort}/"); source.Start(); target.Start();
        var targetRequest = target.GetContextAsync();
        var server = Task.Run(async () =>
        {
            var context = await source.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(context.Request.Headers["Authorization"] == "Bearer fixture-key", "Expected synthetic authorization at configured origin.");
            context.Response.StatusCode = 302; context.Response.RedirectLocation = $"http://127.0.0.1:{targetPort}/stolen"; context.Response.Close();
        });
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new OpenAiSpeech(readKey: () => "fixture-key")
            .SynthesizeAsync("fixture", new(OpenAiBaseUrl: $"http://localhost:{port}/v1"), default));
        await server; Assert.Contains("302", error.Message);
        await Task.Delay(100); Assert.False(targetRequest.IsCompleted, "A redirect must never receive a request or credential.");
        target.Stop(); try { await targetRequest; } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
    }
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }
    [Fact] public void ServiceIdentityConsentAndOldConfigurationStaySeparate()
    {
        var old = JsonSerializer.Deserialize<Settings>("{\"Provider\":2,\"AllowAutomaticEdge\":true,\"AllowAutomaticCloud\":true}")!;
        Assert.True(old.IsValid); Assert.Equal(TtsProvider.Edge, old.Provider); Assert.False(old.AllowAutomaticMiMo); Assert.False(old.AllowAutomaticOpenAi);
        Assert.False((old with { Provider = TtsProvider.MiMo }).AllowsAutomaticProvider);
        Assert.False((old with { Provider = TtsProvider.OpenAiCompatible }).AllowsAutomaticProvider);
        var settings = new Settings(Provider: TtsProvider.OpenAiCompatible, AllowAutomaticOpenAi: true, OpenAiAuthorizedBaseUrl: "https://api.openai.com/v1");
        Assert.True(settings.AllowsAutomaticProvider); Assert.True((settings with { OpenAiBaseUrl = "https://API.OPENAI.COM:443/v1/" }).AllowsAutomaticProvider);
        Assert.False((settings with { OpenAiBaseUrl = "https://different.example/v1" }).AllowsAutomaticProvider);
        Assert.False((settings with { OpenAiBaseUrl = "https://api.openai.com/other" }).AllowsAutomaticProvider);
        Assert.Equal(0, (int)TtsProvider.Windows); Assert.Equal(1, (int)TtsProvider.Azure); Assert.Equal(2, (int)TtsProvider.Edge);
    }
    [Theory] [InlineData("http://remote.example/v1")] [InlineData("https://user:secret@example.org/v1")] [InlineData("https://example.org/v1?key=secret")] [InlineData("file:///C:/secret")]
    public async Task UnsafeBaseUrlCannotSendCredentials(string address)
    {
        Assert.Null(Settings.NormalizeServiceUrl(address)); using var http = new HttpClient(new Handler((_, _) => throw new Exception("must not send")));
        await Assert.ThrowsAsync<UserFeedbackException>(() => Synthesize(false, http, "fixture", new(OpenAiBaseUrl: address)));
    }
    [Theory] [InlineData("http://localhost:8000/v1/")] [InlineData("http://127.0.0.1:8000/v1")] [InlineData("http://[::1]:8000/v1/")]
    public async Task ExplicitLoopbackAddressUsesSingleSpeechRoute(string address)
    {
        using var http = new HttpClient(new Handler((request, _) => { Assert.Equal(Settings.NormalizeServiceUrl(address) + "/audio/speech", request.RequestUri!.AbsoluteUri); return Task.FromResult(Response(false)); }));
        using var audio = await Synthesize(false, http, "fixture", new(OpenAiBaseUrl: address));
    }
    [Fact] public void CredentialNamespacesAreIndependentAndSettingsContainNoKey()
    {
        var id = Guid.NewGuid(); var first = AzureCredentials.OpenAi($"http://localhost:8000/{id}");
        var same = AzureCredentials.OpenAi($"http://LOCALHOST:8000/{id}/"); var other = AzureCredentials.OpenAi($"http://localhost:8000/{id}/other");
        try
        {
            first.Write("fixture-private-key"); Assert.True(same.Read() == "fixture-private-key", "Credential roundtrip failed."); Assert.Null(other.Read());
            first.Write("updated-fixture"); Assert.True(same.Read() == "updated-fixture", "Credential update failed.");
            first.Clear(); Assert.Null(same.Read());
            var path = Path.Combine(Path.GetTempPath(), $"SeleVox-{id}", "settings.json");
            try { var store = new SettingsStore(path); var settings = new Settings(Provider: TtsProvider.MiMo, MiMoVoice: "Mia", MiMoStyle: "calm", MiMoModel: "fixture-next-model", AllowAutomaticMiMo: true); store.Save(settings); Assert.Equal(settings, store.Load().Settings); Assert.DoesNotContain("fixture-private-key", File.ReadAllText(path)); }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        }
        finally { first.Clear(); other.Clear(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class BlockingStream(TaskCompletionSource entered) : Stream
    {
        public bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class OversizeStream : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); buffer.Span.Clear(); position += buffer.Length; return ValueTask.FromResult(position <= HttpSpeech.MaxResponseBytes + 16384 ? buffer.Length : 0); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
