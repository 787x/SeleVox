using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using SeleVox.App.EdgeTransport;
using Xunit.Abstractions;

namespace SeleVox.Tests;

// Opt-in, read-only connection probe. No SSML or selected text is sent.
// Exactly one HTTP query and one WebSocket handshake; never retries a refusal.
public sealed class EdgeHandshakeDiagnosticTests(ITestOutputHelper log)
{
    [EdgeServiceFact, Trait("Category", "EdgeService")]
    public async Task CurrentHandshakeAndServerClockWithoutSendingText()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var queryUri = new Uri($"{Constants.VoiceListUrl}&Sec-MS-GEC={Drm.GenerateSecMsGec()}&Sec-MS-GEC-Version={Constants.SecMsGecVersion}");
        log.WriteLine($"HTTP proxy applies={ !HttpClient.DefaultProxy.IsBypassed(queryUri) }");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
        using var client = new HttpClient(handler);
        foreach (var header in Constants.VoiceHeaders)
            if (header.Key != "Accept-Encoding") client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        using (var response = await client.GetAsync(queryUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            log.WriteLine($"Voice-list HTTP={(int)response.StatusCode}; server Date available={response.Headers.Date.HasValue}");
            if (response.Headers.Date is { } date) log.WriteLine($"Server minus local clock seconds={(date - DateTimeOffset.UtcNow).TotalSeconds:F1}");
        }
        using var socket = new ClientWebSocket(); Communicate.ConfigureSocket(socket);
        var uri = new Uri($"{Constants.WssUrl}&ConnectionId={Guid.NewGuid():N}&Sec-MS-GEC={Drm.GenerateSecMsGec()}&Sec-MS-GEC-Version={Constants.SecMsGecVersion}");
        log.WriteLine($"WebSocket proxy applies={ !HttpClient.DefaultProxy.IsBypassed(uri) }");
        try { await socket.ConnectAsync(uri, timeout.Token); }
        catch (WebSocketException error)
        {
            log.WriteLine($"WebSocket HTTP={(int)socket.HttpStatusCode}; error category={error.WebSocketErrorCode}");
            if (socket.HttpResponseHeaders is { } headers && headers.TryGetValue("Date", out var values) &&
                DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                log.WriteLine($"Handshake server minus local clock seconds={(date - DateTimeOffset.UtcNow).TotalSeconds:F1}");
            Assert.Fail($"Handshake failed: HTTP {(int)socket.HttpStatusCode}, category {error.WebSocketErrorCode}. No text sent.");
        }
        log.WriteLine($"WebSocket HTTP={(int)socket.HttpStatusCode}; state={socket.State}; no text sent.");
        Assert.Equal(WebSocketState.Open, socket.State);
    }
}
