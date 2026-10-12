using System.Net;
using System.Runtime.CompilerServices;
using EdgeTTS.DotNet;
using EdgeTTS.DotNet.Models;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class EdgeSpeechTests
{
    private static readonly Settings Edge = new(Provider: TtsProvider.Edge, EdgeVoice: "zh-CN-XiaoxiaoNeural");
    private static byte[] Tone => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone.mp3"));

    [Theory] [InlineData(0.5, "-50%")] [InlineData(1, "+0%")] [InlineData(1.05, "+5%")] [InlineData(2, "+100%")]
    public void MapsRate(double value, string expected) => Assert.Equal(expected, EdgeSpeech.RateParameter(value));
    [Theory] [InlineData(-100, "-100Hz")] [InlineData(0, "+0Hz")] [InlineData(100, "+100Hz")]
    public void MapsPitch(int value, string expected) => Assert.Equal(expected, EdgeSpeech.PitchParameter(value));

    [Fact] public void LongUnicodeTextKeepsEveryScalarAndXmlEntity()
    {
        var text = string.Concat(Enumerable.Repeat("测试😀<&\"'无空格", 1800));
        var parts = EdgeSpeech.SplitText(text).ToArray(); Assert.True(parts.Length > 1);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, p => { Assert.DoesNotContain("�", p); Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(WebUtility.HtmlEncode(p)), 1, 3500); });
    }
    [Fact] public void RealMp3DecoderAppendsAllIndependentTurnsToCompletePcmWave()
    {
        using var single = EdgeSpeech.DecodeSegments([Tone], default); using var one = new WaveFileReader(single);
        using var many = EdgeSpeech.DecodeSegments(Enumerable.Repeat(Tone, 30), default); using var all = new WaveFileReader(many);
        Assert.Equal(WaveFormatEncoding.Pcm, all.WaveFormat.Encoding); Assert.True(one.Length > 0);
        Assert.Equal(one.Length * 30, all.Length); Assert.Equal(0, all.Length % all.WaveFormat.BlockAlign);
        var read = new byte[all.Length]; Assert.Equal(read.Length, all.Read(read, 0, read.Length));
    }
    [Fact] public async Task ArbitraryNetworkChunksAreAssembledBeforeDecodeAndUseExistingFactory()
    {
        var calls = 0;
        var speech = new EdgeSpeech((_, _, token) => Pieces(token));
        async IAsyncEnumerable<TTSChunk> Pieces([EnumeratorCancellation] CancellationToken token)
        {
            ++calls; var bytes = Tone;
            for (var i = 0; i < bytes.Length; i += 7) { token.ThrowIfCancellationRequested(); yield return new AudioChunk(bytes.Skip(i).Take(7).ToArray()); }
            await Task.Yield(); yield return new TurnEndChunk();
        }
        using var wave = await speech.SynthesizeAsync(new string('中', 3000), Edge, default);
        Assert.True(calls > 1);
        using var group = AudioOutputs.CreatePlayback(wave, Edge with { OutputDeviceIds = ["a", "b"] }, (_, audio, _) =>
        {
            using var reader = new WaveFileReader(audio); Assert.True(reader.Length > 0); return new Player();
        });
        group.Play(); group.Pause(); group.Play(); group.Stop();
    }
    [Fact] public async Task CancellationAfterAudioNeverSucceedsAndIteratorIsReleased()
    {
        using var cancellation = new CancellationTokenSource(); var released = false;
        async IAsyncEnumerable<TTSChunk> Pieces([EnumeratorCancellation] CancellationToken token)
        {
            try { yield return new AudioChunk(Tone); cancellation.Cancel(); await Task.Yield(); yield return new TurnEndChunk(); }
            finally { released = true; }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EdgeSpeech((_, _, t) => Pieces(t)).SynthesizeAsync("fixture", Edge, cancellation.Token));
        Assert.True(released);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task EmptyIncompleteAndInvalidAudioProduceSanitizedFeedback(int mode)
    {
        async IAsyncEnumerable<TTSChunk> Pieces()
        {
            await Task.Yield();
            if (mode == 1) yield return new AudioChunk(Tone);
            if (mode == 2) yield return new AudioChunk([1, 2, 3]);
            if (mode != 1) yield return new TurnEndChunk();
        }
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new EdgeSpeech((_, _, _) => Pieces()).SynthesizeAsync("secret fixture", Edge, default));
        Assert.DoesNotContain("secret", error.Message);
        Assert.Contains(mode == 0 ? "未返回音频" : mode == 1 ? "不完整" : "解码失败", error.Message);
    }
    [Fact] public void TruncatedFinalFrameIsRejected()
    {
        var error = Assert.Throws<UserFeedbackException>(() => EdgeSpeech.DecodeSegments([Tone[..^1]], default));
        Assert.Contains("解码失败", error.Message);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => EdgeSpeech.DecodeSegments([Tone], cancellation.Token));
    }
    [Fact] public async Task TimeoutsCancelNetworkOperationAndDoNotReturnPartialAudio()
    {
        var released = false;
        async IAsyncEnumerable<TTSChunk> Pieces([EnumeratorCancellation] CancellationToken token)
        {
            try { yield return new AudioChunk(Tone); await Task.Delay(Timeout.Infinite, token); }
            finally { released = true; }
        }
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new EdgeSpeech((_, _, t) => Pieces(t), synthesisTimeout: TimeSpan.FromMilliseconds(50)).SynthesizeAsync("fixture", Edge, default));
        Assert.Contains("超时", error.Message); Assert.True(released);
        error = await Assert.ThrowsAsync<UserFeedbackException>(() => new EdgeSpeech(query: async token => { await Task.Delay(Timeout.Infinite, token); return []; }, queryTimeout: TimeSpan.FromMilliseconds(50)).GetVoicesAsync(default));
        Assert.Contains("查询超时", error.Message);
    }
    [Fact] public void RefusalProtocolAndNetworkErrorsDoNotExposeTokensOrText()
    {
        foreach (var error in new Exception[] { new HttpRequestException("secret", null, HttpStatusCode.Forbidden), new System.Net.WebSockets.WebSocketException("403 secret"), new HttpRequestException("secret"), new UnexpectedResponseException("secret") })
        {
            var message = EdgeSpeech.Feedback(error).Message; Assert.DoesNotContain("secret", message);
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden } or System.Net.WebSockets.WebSocketException) Assert.Contains("拒绝访问", message);
        }
    }
    private sealed class Player : IAudioPlayback
    {
        public event Action<Exception?>? Finished { add { } remove { } }
        public void Play() { } public void Pause() { } public void Stop() { } public void Dispose() { }
    }
    [Theory] [InlineData(401)] [InlineData(403)]
    public void ServiceRefusalReportsExactStatusWithoutExposingDetails(int status)
    {
        var error = new EdgeTTS.DotNet.WebSocketException("secret URI and text",
            new HttpRequestException("secret token", null, (HttpStatusCode)status));
        var message = EdgeSpeech.Feedback(error).Message;
        Assert.Contains($"HTTP {status}", message); Assert.Contains("未自动重试", message);
        Assert.DoesNotContain("secret", message);
        Assert.DoesNotContain(status == 401 ? "403" : "401", message);
    }
    [Fact] public async Task VoiceQueryRetriesTransportOnlyOnceWithoutChangingRequestIdentity()
    {
        var count = 0; var urls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            ++count; urls.Add(request.RequestUri!.AbsoluteUri); throw new HttpRequestException("secret");
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => EdgeSpeech.QueryResponseAsync(http, "https://example.invalid/fixture?identity=fixed", default));
        Assert.Equal(2, count); Assert.Single(urls.Distinct());
        count = 0;
        using var refused = new HttpClient(new Handler(_ => { ++count; return new(HttpStatusCode.Forbidden); }));
        using var response = await EdgeSpeech.QueryResponseAsync(refused, "https://example.invalid/fixture", default);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(1, count);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EdgeSpeech.QueryResponseAsync(http, "https://example.invalid/fixture", cancellation.Token));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
}
