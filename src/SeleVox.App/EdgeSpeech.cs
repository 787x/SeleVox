using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EdgeTTS.DotNet;
using EdgeTTS.DotNet.Models;
using NAudio.Wave;
using NAudio.Utils;
using SeleVox.Core;
using Transport = SeleVox.App.EdgeTransport;

namespace SeleVox.App;

internal sealed record EdgeVoiceChoice(string Id, string Label, string Locale);

internal sealed class EdgeSpeech(
    Func<string, Settings, CancellationToken, IAsyncEnumerable<TTSChunk>>? stream = null,
    Func<CancellationToken, Task<IReadOnlyList<EdgeVoiceChoice>>>? query = null,
    TimeSpan? synthesisTimeout = null, TimeSpan? queryTimeout = null)
{
    private const int MaxMp3Bytes = 64 * 1024 * 1024;
    private const int MaxWaveBytes = 256 * 1024 * 1024;

    internal static string RateParameter(double rate)
    {
        if (!double.IsFinite(rate) || rate is < 0.5 or > 2) throw new ArgumentOutOfRangeException(nameof(rate));
        return Signed((int)Math.Round((rate - 1) * 100, MidpointRounding.AwayFromZero)) + "%";
    }
    internal static string PitchParameter(int hz)
    {
        if (hz is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(hz));
        return Signed(hz) + "Hz";
    }
    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);

    // Split before library escaping, by Unicode scalar and escaped UTF-8 size.
    // v0.4.0's internal splitter uses replacement fallback at a UTF-8 boundary.
    internal static IEnumerable<string> SplitText(string text)
    {
        var part = new StringBuilder(); var size = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.ToString();
            var bytes = Encoding.UTF8.GetByteCount(WebUtility.HtmlEncode(value));
            if (size + bytes > 3500) { yield return part.ToString(); part.Clear(); size = 0; }
            part.Append(value); size += bytes;
        }
        if (part.Length > 0) yield return part.ToString();
    }

    public async Task<IReadOnlyList<EdgeVoiceChoice>> GetVoicesAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(queryTimeout ?? TimeSpan.FromSeconds(20));
        try
        {
            var voices = await (query ?? QueryServiceAsync)(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return voices.OrderBy(v => v.Locale).ThenBy(v => v.Label).ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new UserFeedbackException("Edge 语音查询超时，请检查网络或稍后刷新。", ServiceFailure.Timeout); }
        catch (Exception ex) { throw Feedback(ex); }
    }

    private static async Task<IReadOnlyList<EdgeVoiceChoice>> QueryServiceAsync(CancellationToken cancellation)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        var uri = $"{Transport.Constants.VoiceListUrl}&Sec-MS-GEC={Transport.Drm.GenerateSecMsGec()}&Sec-MS-GEC-Version={Transport.Constants.SecMsGecVersion}";
        foreach (var header in Transport.Constants.VoiceHeaders)
            if (header.Key != "Accept-Encoding") client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await QueryResponseAsync(client, uri, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var voices = JsonSerializer.Deserialize<List<Voice>>(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        if (voices is null) throw new UnexpectedResponseException("Invalid voice response.");
        return voices.Where(v => !string.IsNullOrWhiteSpace(v.ShortName) && !string.IsNullOrWhiteSpace(v.Locale))
            .Select(v => new EdgeVoiceChoice(v.ShortName, $"{v.FriendlyName} ({v.Locale})", v.Locale)).ToArray();
    }

    internal static async Task<HttpResponseMessage> QueryResponseAsync(HttpClient client, string uri, CancellationToken cancellation)
    {
        try { return await client.GetAsync(uri, cancellation).ConfigureAwait(false); }
        catch (HttpRequestException error) when (error.StatusCode is null && !cancellation.IsCancellationRequested)
        {
            await Task.Delay(500, cancellation).ConfigureAwait(false);
            return await client.GetAsync(uri, cancellation).ConfigureAwait(false);
        }
    }

    public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.EdgeVoice)) throw new UserFeedbackException("请查询并选择 Edge 在线语音，再保存设置。");
        if (text.Length > 50000) throw new UserFeedbackException("Edge 单次朗读最多支持 50000 个字符，请缩短选区。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(synthesisTimeout ?? TimeSpan.FromMinutes(3));
        try
        {
            var segments = new List<byte[]>(); long total = 0;
            foreach (var part in SplitText(text))
            {
                using var turnTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                turnTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var chunks = stream is null
                    ? new Transport.Communicate(part, settings.EdgeVoice, RateParameter(settings.Rate), pitch: PitchParameter(settings.EdgePitchHz)).StreamAsync(turnTimeout.Token)
                    : stream(part, settings, turnTimeout.Token);
                using var pending = new MemoryStream(); var completed = false;
                await foreach (var chunk in chunks.WithCancellation(turnTimeout.Token).ConfigureAwait(false))
                {
                    turnTimeout.Token.ThrowIfCancellationRequested();
                    if (chunk is AudioChunk audio)
                    {
                        if (completed) throw new UnexpectedResponseException("Audio after turn completion.");
                        total += audio.Data.Length;
                        if (total > MaxMp3Bytes) throw new UserFeedbackException("Edge 音频过长，请缩短选区。");
                        pending.Write(audio.Data);
                    }
                    else if (chunk is TurnEndChunk) completed = true;
                }
                turnTimeout.Token.ThrowIfCancellationRequested();
                if (pending.Length == 0) throw new NoAudioReceivedException("No audio.");
                if (!completed) throw new UnexpectedResponseException("Incomplete audio turn.");
                segments.Add(pending.ToArray());
            }
            timeout.Token.ThrowIfCancellationRequested();
            return await Task.Run(() => DecodeSegments(segments, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new UserFeedbackException("Edge 合成超时，请检查网络或缩短选区。", ServiceFailure.Timeout); }
        catch (Exception ex) { throw Feedback(ex); }
    }

    internal static Stream DecodeSegments(IEnumerable<byte[]> segments, CancellationToken cancellation)
    {
        var wave = new MemoryStream(); WaveFileWriter? writer = null;
        try
        {
            foreach (var segment in segments)
            {
                cancellation.ThrowIfCancellationRequested();
                // Each completed service turn is an independent MP3. Never reset a
                // decoder at arbitrary network-chunk boundaries or concatenate MP3 headers.
                using var validation = new MemoryStream(segment, false);
                while (validation.Position < validation.Length)
                {
                    cancellation.ThrowIfCancellationRequested(); var start = validation.Position;
                    var frame = Mp3Frame.LoadFromStream(validation);
                    if (frame is null || validation.Position - start != frame.FrameLength)
                        throw new InvalidDataException();
                }
                using var compressed = new MemoryStream(segment, false);
                using var mp3 = new Mp3FileReaderBase(compressed, format => new AcmMp3FrameDecompressor(format));
                if (writer is null) writer = new WaveFileWriter(new IgnoreDisposeStream(wave), mp3.WaveFormat);
                else if (!writer.WaveFormat.Equals(mp3.WaveFormat)) throw new InvalidDataException();
                var buffer = new byte[16384]; long decoded = 0; int count;
                while ((count = mp3.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (wave.Length + count > MaxWaveBytes) throw new UserFeedbackException("Edge 音频过长，请缩短选区。");
                    writer.Write(buffer, 0, count); decoded += count;
                }
                if (decoded == 0 || decoded % mp3.WaveFormat.BlockAlign != 0) throw new InvalidDataException();
            }
            writer?.Dispose(); writer = null;
            cancellation.ThrowIfCancellationRequested();
            wave.Position = 0;
            using (var reader = new WaveFileReader(new IgnoreDisposeStream(wave)))
                if (reader.Length == 0 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm) throw new InvalidDataException();
            wave.Position = 0; return wave;
        }
        catch (OperationCanceledException) { wave.Dispose(); throw; }
        catch (UserFeedbackException) { wave.Dispose(); throw; }
        catch { wave.Dispose(); throw new UserFeedbackException("Edge 音频解码失败，收到的 MP3 不完整或不兼容，请重试。"); }
        finally { try { writer?.Dispose(); } catch { /* Original sanitized failure remains authoritative. */ } }
    }

    internal static UserFeedbackException Feedback(Exception error)
    {
        if (error is UserFeedbackException feedback) return feedback;
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: not null } http)
                return http.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? Refused((int)http.StatusCode.Value) : UserFeedbackException.Http((int)http.StatusCode.Value);
        }
        return new(error switch
        {
            NoAudioReceivedException => "Edge 未返回音频，请检查所选语音和文本语言。",
            HttpRequestException { StatusCode: not null } => "Edge 服务拒绝请求，请稍后重试或切回本地 TTS。",
            HttpRequestException or System.Net.WebSockets.WebSocketException or EdgeTTS.DotNet.WebSocketException => "无法连接 Edge 在线语音，请检查网络或稍后重试。",
            ArgumentException => "Edge 语音或参数无效，请重新查询选择语音。",
            _ => "Edge 响应不完整或协议不兼容，请重试或切回 Windows 本地 TTS。"
        }, error is HttpRequestException or System.Net.WebSockets.WebSocketException or EdgeTTS.DotNet.WebSocketException
            ? ServiceFailure.Connection : error is ArgumentException ? ServiceFailure.Parameters : ServiceFailure.InvalidResponse);
    }
    private static UserFeedbackException Refused(int status) => new(
        $"Edge 服务拒绝访问（HTTP {status}）。此实验性在线接入当前不可用，请切回 Windows 本地 TTS；未自动重试或转发文本。", ServiceFailure.Authentication, status);
}
