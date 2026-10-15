using System.Net;
using System.Text;
using System.Xml.Linq;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class AzureSpeechTests
{
    [Fact] public async Task QueriesRegionVoicesAndReturnsWaveWithoutDirectPlayback()
    {
        var requests = new List<string>();
        using var handler = new Handler(async (request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("[{\"ShortName\":\"zh-CN-fixture\",\"DisplayName\":\"Fixture\",\"Locale\":\"zh-CN\"}]") };
            Assert.Equal("riff-24khz-16bit-mono-pcm", request.Headers.GetValues("X-Microsoft-OutputFormat").Single());
            var ssml = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Contains("<private & fixture>", ssml.Root!.Value);
            using var wave = MultiOutputTests.Fixture();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(wave.ToArray()) };
        });
        using var http = new HttpClient(handler); var azure = new AzureSpeech(http, () => "fixture-key");
        var voices = await azure.GetVoicesAsync("eastasia", default); Assert.Equal("zh-CN-fixture", Assert.Single(voices).Id);
        using var audio = await azure.SynthesizeAsync("<private & fixture>", new(Provider: TtsProvider.Azure, AzureRegion: "eastasia", AzureVoice: voices[0].Id), default);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(new BinaryReader(audio).ReadBytes(4)));
        Assert.All(requests, r => Assert.StartsWith("https://eastasia.tts.speech.microsoft.com/", r));
    }
    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)] [InlineData(400)]
    public async Task ServiceErrorsNeverExposeResponseContent(int code)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("secret key and selected text") }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new AzureSpeech(http, () => "fixture").GetVoicesAsync("eastasia", default));
        Assert.DoesNotContain("secret", error.Message);
    }
    [Fact] public async Task CancelAndNetworkFailureGiveControlledResults()
    {
        using var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using var http = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var pending = new AzureSpeech(http, () => "fixture").GetVoicesAsync("eastasia", cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var failed = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private")));
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => new AzureSpeech(failed, () => "fixture").GetVoicesAsync("eastasia", default));
        Assert.Contains("网络", error.Message); Assert.DoesNotContain("private", error.Message);
    }
    [Theory] [InlineData("eastasia.attacker/path")] [InlineData("https://eastasia")] [InlineData("")]
    public async Task InvalidRegionNeverSendsKey(string region)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Must not send")));
        await Assert.ThrowsAsync<UserFeedbackException>(() => new AzureSpeech(http, () => "fixture").GetVoicesAsync(region, default));
    }
    [Theory] [InlineData(0.5, "-50%")] [InlineData(1, "0%")] [InlineData(2, "+100%")]
    public void RateIsMappedAndTextEscaped(double rate, string expected)
    {
        var xml = XDocument.Parse(AzureSpeech.CreateSsml("<a & b>", "fixture", rate)); XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        Assert.Equal(expected, xml.Descendants(ns + "prosody").Single().Attribute("rate")!.Value);
        Assert.Equal("<a & b>", xml.Root!.Value);
    }
    [Fact] public void RealCredentialManagerRoundTripUpdateAndClear()
    {
        var store = new AzureCredentials("SeleVox/tests/" + Guid.NewGuid());
        try
        {
            Assert.Null(store.Read()); store.Write("fixture-假密钥"); Assert.Equal("fixture-假密钥", store.Read());
            store.Write("updated-fixture"); Assert.Equal("updated-fixture", store.Read()); store.Clear(); Assert.Null(store.Read()); store.Clear();
        }
        finally { store.Clear(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
