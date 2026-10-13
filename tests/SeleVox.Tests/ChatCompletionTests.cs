using System.Net;
using System.Text;
using System.Text.Json;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class ChatCompletionTests
{
    internal static Settings Options => new(LlmModel: "test-model", LlmSystemPrompt: "system only");
    internal static string Event(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } } }) + "\r\n\r\n";
    internal const string Finish = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    [Theory] [InlineData(1)] [InlineData(7)] [InlineData(8192)]
    public async Task Utf8NetworkFragmentsMultilineAndIncrementalDisplay(int fragment)
    {
        var firstObserved = false;
        var stream = new FragmentStream(Event("中文🌟") + ":heartbeat\r\n\r\ndata: {\"choices\": [\r\ndata: {\"index\":0,\"delta\":{\"content\":\"answer\"},\"finish_reason\":null}]}\r\n\r\n" + Finish, fragment);
        using var http = new HttpClient(new Handler(_ => Response(stream)));
        var result = await new ChatCompletionClient(http).GenerateAsync("question", Options, "fake-key", value =>
        { if (value == "中文🌟") { firstObserved = true; Assert.True(stream.Position < stream.Length || fragment == 8192); } }, default);
        Assert.Equal("中文🌟answer", result); Assert.True(firstObserved);
    }
    [Fact] public async Task RequestHasSeparateRolesAndNoOptionalParameters()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("fake-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var body = json.RootElement;
            Assert.Equal(new[] { "model", "stream", "messages" }, body.EnumerateObject().Select(p => p.Name));
            var messages = body.GetProperty("messages"); Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("system", messages[0].GetProperty("role").GetString()); Assert.Equal("system only", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString()); Assert.Equal("question", messages[1].GetProperty("content").GetString());
            return Response(new FragmentStream(Event("") + Event("answer") + Finish, 1));
        }));
        Assert.Equal("answer", await new ChatCompletionClient(http).GenerateAsync("question", Options, "fake-key", _ => { }, default));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task JsonResponseIsConsumedWithoutResubmitting(bool streaming)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            ++requests;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") };
        }));
        Assert.Equal("answer", await new ChatCompletionClient(http).GenerateAsync("q", Options with { LlmStreaming = streaming }, "key", _ => { }, default));
        Assert.Equal(1, requests);
    }
    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    [InlineData("data: {bad}\n\n")]
    [InlineData("data: {\"error\":{\"message\":\"private server text\"}}\n\n")]
    public async Task TruncatedOrInvalidAnswerFailsAndPreservesAcceptedDeltas(string tail)
    {
        var partial = "";
        using var http = new HttpClient(new Handler(_ => Response(new FragmentStream(Event("partial") + tail, 3))));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient(http).GenerateAsync("q", Options, "key", text => partial += text, default));
        Assert.Equal("partial", partial); Assert.DoesNotContain("private server text", error.Message);
    }
    [Fact] public async Task EmptyCompletedAnswerFails()
    {
        using var http = new HttpClient(new Handler(_ => Response(new FragmentStream(Finish, 1))));
        await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient(http).GenerateAsync("q", Options, "key", _ => { }, default));
    }
    [Theory] [InlineData(302)] [InlineData(401)] [InlineData(429)] [InlineData(500)]
    public async Task HttpFailureNeverRetriesOrDisclosesBody(int status)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            ++requests; var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret server body") };
            response.Headers.Location = new Uri("https://unapproved.invalid"); return response;
        }));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient(http).GenerateAsync("q", Options, "key", _ => { }, default));
        Assert.Equal(1, requests); Assert.DoesNotContain("secret", error.Message);
    }
    [Theory] [InlineData(" ", "key", "https://api.openai.com/v1", "model")]
    [InlineData("q", "", "https://api.openai.com/v1", "model")]
    [InlineData("q", "key", "http://remote.invalid", "model")]
    [InlineData("q", "key", "https://user:pass@example.com", "model")]
    [InlineData("q", "key", "https://example.com/?query", "model")]
    [InlineData("q", "key", "https://example.com", "")]
    public async Task InvalidConfigurationDoesNotSend(string question, string key, string address, string model)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("must not send")));
        await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient(http).GenerateAsync(question, Options with { LlmBaseUrl = address, LlmModel = model }, key, _ => { }, default));
    }
    [Fact] public async Task InputEventAndOutputHaveBounds()
    {
        await Reject(new string('q', ChatCompletionClient.MaxText + 1), Finish);
        await Reject("q", ":" + new string('a', ChatCompletionClient.MaxEventBytes) + "\n\n");
        await Reject("q", Event(new string('a', ChatCompletionClient.MaxText + 1)) + Finish);
        await Reject("q", string.Concat(Enumerable.Repeat(":" + new string('a', 1000) + "\n\n", 4200)));
        static async Task Reject(string question, string body)
        {
            using var http = new HttpClient(new Handler(_ => Response(new FragmentStream(body, 8192))));
            await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient(http).GenerateAsync(question, Options, "key", _ => { }, default));
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task IdleAndTotalBudgetsInterruptHangingRead(bool total)
    {
        using var http = new HttpClient(new Handler(_ => Response(new HangingStream())));
        var client = new ChatCompletionClient(http, TimeSpan.FromMilliseconds(total ? 1000 : 40), TimeSpan.FromMilliseconds(total ? 40 : 1000));
        await Assert.ThrowsAsync<UserFeedbackException>(() => client.GenerateAsync("q", Options, "key", _ => { }, default).WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact] public async Task CancellationInterruptsHeadersAndBody()
    {
        foreach (var headers in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource(40);
            using var http = new HttpClient(new Handler(async (_, token) =>
            { if (headers) await Task.Delay(Timeout.Infinite, token); return Response(new HangingStream()); }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatCompletionClient(http).GenerateAsync("q", Options, "key", _ => { }, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }
    internal static HttpResponseMessage Response(Stream stream)
    {
        var content = new StreamContent(stream); content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }
    internal sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : this((r, _) => respond(r)) { }
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    internal sealed class FragmentStream(string body, int fragment) : MemoryStream(Encoding.UTF8.GetBytes(body))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
    }
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
