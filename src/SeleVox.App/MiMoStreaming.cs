using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed partial class MiMoSpeech
{
    private static readonly HttpClient StreamingClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation,
        MiMoStreamingMetrics? metrics = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (settings.MiMoModel != "mimo-v2.5-tts") throw new UserFeedbackException("当前只支持 mimo-v2.5-tts 流式协议；自定义模型请使用非流式模式。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > HttpSpeech.MaxTextLength) throw new UserFeedbackException("文本为空或过长，请分次朗读。");
        var style = GetStyle(settings); long audioBytes = 0;
        var prefix = MiMoStyleTags.GetPrefix(settings.MiMoOverallStyles);
        var textBudget = GetTextBudget(prefix);
        var key = (readKey ?? AzureCredentials.MiMo().Read)();
        if (string.IsNullOrWhiteSpace(key)) throw new UserFeedbackException("请先保存 MiMo API Key。");
        try
        {
            foreach (var part in HttpSpeech.SplitText(text, textBudget, true, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.xiaomimimo.com/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new StringContent(JsonSerializer.Serialize(CreateRequest(prefix + part, settings.MiMoVoice, style, settings.MiMoModel, true)), Encoding.UTF8, "application/json");
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellation); headers.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await (client ?? StreamingClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                headers.CancelAfter(Timeout.InfiniteTimeSpan);
                if (!response.IsSuccessStatusCode) throw new UserFeedbackException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "MiMo 认证失败，请检查 API Key 和权限。",
                    HttpStatusCode.TooManyRequests => "MiMo 请求过多或额度不足，请稍后重试。",
                    _ => $"MiMo 服务拒绝流式请求（HTTP {(int)response.StatusCode}），不会重试已朗读的文本。"
                });
                if (response.Content.Headers.ContentLength > HttpSpeech.MaxResponseBytes) throw new UserFeedbackException("MiMo 流式响应过大。");
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new UserFeedbackException("MiMo 未返回 SSE 流式音频。");
                if (metrics is not null) ++metrics.Requests;
                using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                byte? carry = null; long segmentBytes = 0;
                await foreach (var chunk in ReadSseAudioAsync(input, cancellation).ConfigureAwait(false))
                {
                    if (metrics is not null) metrics.FirstAudioMilliseconds ??= metrics.ElapsedMilliseconds;
                    cancellation.ThrowIfCancellationRequested(); audioBytes += chunk.Length; segmentBytes += chunk.Length;
                    if (audioBytes > HttpSpeech.MaxAudioBytes) throw new UserFeedbackException("MiMo 流式音频过大，请分次朗读。");
                    var pcm = chunk;
                    if (carry is { } previous)
                    { pcm = new byte[chunk.Length + 1]; pcm[0] = previous; chunk.CopyTo(pcm, 1); carry = null; }
                    if (pcm.Length % 2 != 0) carry = pcm[^1];
                    await buffer.WriteAsync(pcm.AsMemory(0, pcm.Length & ~1), cancellation).ConfigureAwait(false);
                }
                if (carry is not null) throw new UserFeedbackException("MiMo 流式音频结尾未对齐，响应不完整。");
                if (segmentBytes == 0) throw new UserFeedbackException("MiMo 流未返回音频。");
            }
            cancellation.ThrowIfCancellationRequested();
            if (metrics is not null) metrics.NetworkCompleteMilliseconds = metrics.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new UserFeedbackException("MiMo 流式请求超时，已停止，未重试已播放内容。"); }
        catch (HttpRequestException) { throw new UserFeedbackException("MiMo 流式连接失败，请检查网络。"); }
        catch (IOException) { throw new UserFeedbackException("MiMo 流式连接中断，未重试已播放内容。"); }
        catch (FormatException) { throw new UserFeedbackException("MiMo 流式音频或 API Key 格式无效。"); }
        catch (JsonException) { throw new UserFeedbackException("MiMo SSE 音频响应无效。"); }
        catch (InvalidOperationException) { throw new UserFeedbackException("MiMo SSE 音频结构无效。"); }
        catch (DecoderFallbackException) { throw new UserFeedbackException("MiMo SSE 文本编码无效。"); }
    }

    // Byte-fragment agnostic SSE framing. Bounded lines/events; CR, LF and CRLF all work.
    internal static async IAsyncEnumerable<byte[]> ReadSseAudioAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellation,
        TimeSpan? idleTimeout = null)
    {
        const int MaxEventBytes = 2 * 1024 * 1024;
        var block = new byte[8192]; using var line = new MemoryStream(); var data = new StringBuilder();
        var utf8 = new UTF8Encoding(false, true); long received = 0; var afterCr = false; var eventBytes = 0;
        var activeRead = TimeSpan.Zero;
        while (true)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(45));
            var clock = Stopwatch.StartNew();
            var read = await input.ReadAsync(block, budget.Token).ConfigureAwait(false); activeRead += clock.Elapsed;
            if (activeRead > TimeSpan.FromMinutes(10)) throw new UserFeedbackException("MiMo 流式接收耗时超限。");
            if (read == 0) throw new UserFeedbackException("MiMo SSE 未收到正常完成标记，流已中断。");
            received += read;
            if (received > HttpSpeech.MaxResponseBytes) throw new UserFeedbackException("MiMo SSE 响应过大。");
            for (var i = 0; i < read; ++i)
            {
                cancellation.ThrowIfCancellationRequested(); var ch = block[i];
                if (afterCr && ch == '\n') { afterCr = false; continue; }
                afterCr = false;
                if (ch is not (13 or 10))
                {
                    if (line.Length >= MaxEventBytes || ++eventBytes > MaxEventBytes) throw new UserFeedbackException("MiMo SSE 事件过大。");
                    line.WriteByte(ch); continue;
                }
                afterCr = ch == 13;
                var text = utf8.GetString(line.GetBuffer(), 0, (int)line.Length); line.SetLength(0);
                if (text.Length == 0)
                {
                    eventBytes = 0;
                    if (data.Length == 0) continue;
                    var payload = data.ToString().Trim(); data.Clear();
                    if (payload == "[DONE]") yield break;
                    if (payload.Length == 0) continue;
                    using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); var root = json.RootElement;
                    if (root.TryGetProperty("error", out _)) throw new UserFeedbackException("MiMo 服务报告流式错误，已停止朗读。");
                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                        throw new UserFeedbackException("MiMo SSE 响应缺少 choices。");
                    if (choices.GetArrayLength() == 0) continue;
                    if (choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null &&
                        (finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop"))
                        throw new UserFeedbackException("MiMo 合成未正常结束，已停止接收后续音频。");
                    if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind == JsonValueKind.Null) continue;
                    if (!delta.TryGetProperty("audio", out var audio) || audio.ValueKind == JsonValueKind.Null) continue;
                    if (!audio.TryGetProperty("data", out var encoded)) throw new UserFeedbackException("MiMo SSE 音频块缺少数据。");
                    var base64 = encoded.GetString();
                    if (string.IsNullOrWhiteSpace(base64)) continue;
                    var pcm = DecodeBase64(base64, cancellation);
                    if (pcm.Length > 0) yield return pcm;
                }
                else if (text == "data" || text.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = text.Length > 4 ? text[5..] : "";
                    if (value.StartsWith(' ')) value = value[1..];
                    if (data.Length > 0) data.Append('\n'); data.Append(value);
                }
            }
        }
    }
}

// Opt-in diagnostics contain timing/counts only, never text, credentials or HTTP bodies.
internal sealed class MiMoStreamingMetrics
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    internal double ElapsedMilliseconds => clock.Elapsed.TotalMilliseconds;
    internal double? FirstAudioMilliseconds, NetworkCompleteMilliseconds;
    internal int Requests;
}
