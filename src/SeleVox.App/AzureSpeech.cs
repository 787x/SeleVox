using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed class AzureSpeech(HttpClient? client = null, Func<string?>? readKey = null)
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly HttpClient http = client ?? SharedClient;
    private readonly Func<string?> credential = readKey ?? new AzureCredentials().Read;
    public async Task<IReadOnlyList<VoiceChoice>> GetVoicesAsync(string region, CancellationToken cancellation)
    {
        using var response = await SendAsync(region, "voices/list", null, cancellation).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            return json.RootElement.EnumerateArray().Select(v => new VoiceChoice(v.GetProperty("ShortName").GetString()!,
                $"{v.GetProperty("DisplayName").GetString()} ({v.GetProperty("Locale").GetString()})")).ToArray();
        }
        catch (JsonException) { throw new UserFeedbackException("Azure 语音列表响应无效，请稍后重试。"); }
    }
    internal static string CreateSsml(string text, string voice, double rate)
    {
        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        // The voice supplies its locale. Escaping is performed by XML, never by string replacement.
        return new XElement(ns + "speak", new XAttribute("version", "1.0"), new XAttribute(XNamespace.Xml + "lang", "en-US"),
            new XElement(ns + "voice", new XAttribute("name", voice),
                new XElement(ns + "prosody", new XAttribute("rate", ((rate - 1) * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%"), text)))
            .ToString(SaveOptions.DisableFormatting);
    }
    public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.AzureVoice)) throw new UserFeedbackException("请查询并选择 Azure 区域可用的语音，再保存设置。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await SendAsync(settings.AzureRegion, "v1", CreateSsml(text, settings.AzureVoice, settings.Rate), budget.Token).ConfigureAwait(false);
            var bytes = await HttpSpeech.ReadBoundedAsync(response.Content, budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            return new MemoryStream(bytes, writable: false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new UserFeedbackException("Azure 请求超时，请稍后主动重试。", ServiceFailure.Timeout); }
    }
    private async Task<HttpResponseMessage> SendAsync(string region, string route, string? ssml, CancellationToken cancellation)
    {
        if (!Settings.IsRegionValid(region)) throw new UserFeedbackException("请填写 Azure Speech Region 标识，例如 eastasia。");
        var key = credential();
        if (string.IsNullOrWhiteSpace(key)) throw new UserFeedbackException("请先在设置中保存 Azure Speech Key。");
        using var request = new HttpRequestMessage(ssml is null ? HttpMethod.Get : HttpMethod.Post,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/{route}");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Headers.Add("User-Agent", "SeleVox");
        if (ssml is not null)
        {
            request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
            request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
        }
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new UserFeedbackException("Azure 请求超时，请检查网络或稍后重试。", ServiceFailure.Timeout); }
        catch (HttpRequestException) { throw new UserFeedbackException("无法连接 Azure Speech，请检查网络及 Region。", ServiceFailure.Connection); }
        if (response.IsSuccessStatusCode) return response;
        var code = response.StatusCode; response.Dispose();
        throw UserFeedbackException.Http((int)code);
    }
}
