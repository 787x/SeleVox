using System.Net;
using System.Net.Sockets;
using System.Text;
using SeleVox.Core;

namespace SeleVox.Tests;

// Real loopback HTTP, fixed synthetic content, no external service or paid credentials.
[Trait("Category", "Offline")]
public sealed class ChatNetworkTests
{
    internal sealed class Server : IDisposable
    {
        private readonly HttpListener listener = new();
        internal string Address { get; }
        internal Server()
        {
            var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            Address = $"http://127.0.0.1:{number}"; listener.Prefixes.Add(Address + "/"); listener.Start();
        }
        internal Task<HttpListenerContext> Next() => listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose() { listener.Close(); }
        internal static async Task Write(HttpListenerResponse response, string text)
        { await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text)); await response.OutputStream.FlushAsync(); }
    }
    [Fact] public async Task ProductionTransportDoesNotFollowRedirectOrForwardBearer()
    {
        using var first = new Server(); using var target = new Server();
        var serving = Task.Run(async () =>
        {
            var context = await first.Next(); Assert.Equal("Bearer fake-test-key", context.Request.Headers["Authorization"]);
            context.Response.StatusCode = 307; context.Response.RedirectLocation = target.Address + "/unexpected"; context.Response.Close();
        });
        var redirected = target.Next();
        await Assert.ThrowsAsync<UserFeedbackException>(() => new ChatCompletionClient().GenerateAsync("q", ChatCompletionTests.Options with { LlmBaseUrl = first.Address }, "fake-test-key", _ => { }, default));
        await serving; await Task.Delay(150); Assert.False(redirected.IsCompleted);
        // Observe cancellation caused by closing the listener, without leaving an unobserved task.
        target.Dispose(); try { await redirected; } catch (Exception) { }
    }
    [Fact] public async Task RealHttpDisplaysBeforeNetworkCompletionAndCancelsOpenStream()
    {
        using var server = new Server(); using var cancel = new CancellationTokenSource();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            var context = await server.Next(); context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
            await Server.Write(context.Response, ChatCompletionTests.Event("partial")); await release.Task; context.Response.Close();
        });
        var request = new ChatCompletionClient().GenerateAsync("q", ChatCompletionTests.Options with { LlmBaseUrl = server.Address }, "fake-key", _ => observed.TrySetResult(), cancel.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(request.IsCompleted); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { release.TrySetResult(); await serving; }
    }
}
