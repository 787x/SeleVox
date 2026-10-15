using System.Net;
using System.Text;
using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class MiMoStreamingTests
{
    internal static string Event(byte[] bytes) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { audio = new { data = Convert.ToBase64String(bytes) } } } } }) + "\r\n\r\n";
    internal static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } } };
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    [Theory] [InlineData(1)] [InlineData(7)] [InlineData(8192)]
    public async Task ArbitraryFragmentsCrLfCommentsMultipleDataAndEmptyDeltas(int fragment)
    {
        var text = ": comment\r\nid: ignored\r\ndata: {\"choices\":[]}\r\n\r\n" +
            "data: {\"choices\":[{\"delta\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"audio\":{\"data\":\"\"}}}]}\r\r" +
            Event([1, 2, 3]) + Event([4, 5, 6]) +
            "data: {\"choices\":\ndata: [{\"delta\":{}}]}\n\n" + "data: [DONE]\n\n";
        using var input = new FragmentStream(Encoding.UTF8.GetBytes(text), fragment); var actual = new List<byte>();
        await foreach (var bytes in MiMoSpeech.ReadSseAudioAsync(input, default)) actual.AddRange(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, actual);
    }
    [Fact] public async Task IncrementalPcmArrivesBeforeServerCompletionAndBeforeNextSegment()
    {
        var first = new byte[9600]; first[0] = 12; first[1] = 34;
        var server = new DelayedStream(Event(first), "data: [DONE]\n\n"); var requests = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.True(request.Headers.Authorization?.Parameter == "fixture", "Expected synthetic credential.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = json.RootElement;
            Assert.True(root.GetProperty("stream").GetBoolean()); Assert.Equal("pcm16", root.GetProperty("audio").GetProperty("format").GetString());
            Assert.Equal("冰糖", root.GetProperty("audio").GetProperty("voice").GetString());
            Assert.Equal("assistant", root.GetProperty("messages")[0].GetProperty("role").GetString());
            requests.Add(root.GetProperty("messages")[0].GetProperty("content").GetString()!);
            return requests.Count == 1 ? Response(server) : Response(new MemoryStream(Encoding.UTF8.GetBytes(Event([56, 78]) + "data: [DONE]\n\n")));
        }));
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        var text = new string('中', 1100); var pending = new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync(text, new(), buffer, default);
        await buffer.WaitForReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(pending.IsCompleted); Assert.Single(requests);
        var actual = new byte[9600]; reader.Read(actual); Assert.Equal(first, actual);
        server.Release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(2)); buffer.Complete();
        Assert.Equal(text, string.Concat(requests)); Assert.Equal(2, requests.Count); var tail = new byte[2]; Assert.Equal(2, reader.Read(tail)); Assert.Equal(new byte[] { 56, 78 }, tail);
    }
    [Fact] public async Task OddEventChunksAreReassembledWithoutBreakingSamples()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new FragmentStream(Encoding.UTF8.GetBytes(Event([1]) + Event([2, 3]) + Event([4]) + "data: [DONE]\n\n"), 1)))));
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        await new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, default); buffer.Complete();
        var actual = new byte[4]; Assert.Equal(4, reader.Read(actual)); Assert.Equal(new byte[] { 1, 2, 3, 4 }, actual);
    }
    [Theory] [InlineData("data: {bad json}\n\n")] [InlineData("data: {\"error\":\"private\"}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"audio\":{\"data\":\"private!\"}}}]}\n\n")]
    [InlineData("data: [DONE]\n\n")] [InlineData("")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")]
    public async Task InvalidOrEmptyStreamIsNotCompleteSuccess(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new MemoryStream(Encoding.UTF8.GetBytes(body))))));
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, default));
        Assert.DoesNotContain("private", error.Message);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingDoneAndUnalignedFinalSampleAreRejected(bool unaligned)
    {
        var body = Event(unaligned ? [1, 2, 3] : [1, 2]) + (unaligned ? "data: [DONE]\n\n" : "");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new MemoryStream(Encoding.UTF8.GetBytes(body))))));
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        await Assert.ThrowsAsync<UserFeedbackException>(() => new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, default));
    }
    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)] [InlineData(302)]
    public async Task ServiceErrorsAreSanitizedAndNeverRetried(int code)
    {
        var calls = 0; using var http = new HttpClient(new Handler((_, _) => { ++calls; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("private") }); }));
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, default));
        Assert.DoesNotContain("private", error.Message); Assert.Equal(1, calls);
    }
    [Fact] public async Task CancelPendingReadReleasesResponseAndDoesNotAppendLateData()
    {
        var input = new DelayedStream(Event(new byte[9600]), Event([1, 2]) + "data: [DONE]\n\n");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(input))));
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader(); using var cancellation = new CancellationTokenSource();
        var pending = new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, cancellation.Token);
        await buffer.WaitForReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); input.Release.TrySetResult(); Assert.Equal(9600, buffer.ProducedBytes); Assert.True(input.Disposed);
    }
    [Fact] public async Task EventSizeAndIdleReadsAreBoundedAndCustomModelIsRejected()
    {
        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('a', MiMoSpeech.MaxSseEventBytes)));
        await Assert.ThrowsAsync<UserFeedbackException>(async () => { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(oversized, default)) { } });
        using var blocked = new DelayedStream("", "");
        var timeout = await Assert.ThrowsAsync<UserFeedbackException>(async () => { await foreach (var _ in MiMoSpeech.ReadSseAudioAsync(blocked, default, TimeSpan.FromMilliseconds(30))) { } });
        Assert.Contains("首次音频接收", timeout.Message);
        using var never = new HttpClient(new Handler((_, _) => throw new Exception("must not send"))); using var buffer = new StreamingPcmBuffer();
        await Assert.ThrowsAsync<UserFeedbackException>(() => new MiMoSpeech(never, () => "fixture").SynthesizeStreamingAsync("fixture", new(MiMoModel: "future-model"), buffer, default));
    }
    internal sealed class FragmentStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
    }
    internal sealed class DelayedStream(string initial, string tail) : Stream
    {
        private readonly MemoryStream head = new(Encoding.UTF8.GetBytes(initial)); private readonly MemoryStream rest = new(Encoding.UTF8.GetBytes(tail));
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var count = await head.ReadAsync(buffer, cancellationToken); if (count > 0) return count; await Release.Task.WaitAsync(cancellationToken); return await rest.ReadAsync(buffer, cancellationToken); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; head.Dispose(); rest.Dispose(); base.Dispose(disposing); }
    }
}
