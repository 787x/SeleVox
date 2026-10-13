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
    internal const int MaxSseEventBytes = 16 * 1024 * 1024;
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
                if (metrics is not null) ++metrics.Requests;
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellation); headers.CancelAfter(TimeSpan.FromSeconds(30));
                HttpResponseMessage received;
                try { received = await (client ?? StreamingClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    if (metrics is not null) metrics.TimeoutStage = "响应头";
                    throw new UserFeedbackException("MiMo 等待响应头超时，已停止，未重试已播放内容。");
                }
                using var response = received;
                if (metrics is not null) metrics.ResponseHeadersMilliseconds ??= metrics.ElapsedMilliseconds;
                headers.CancelAfter(Timeout.InfiniteTimeSpan);
                if (!response.IsSuccessStatusCode) throw new UserFeedbackException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "MiMo 认证失败，请检查 API Key 和权限。",
                    HttpStatusCode.TooManyRequests => "MiMo 请求过多或额度不足，请稍后重试。",
                    _ => $"MiMo 服务拒绝流式请求（HTTP {(int)response.StatusCode}），不会重试已朗读的文本。"
                });
                if (response.Content.Headers.ContentLength > HttpSpeech.MaxResponseBytes) throw new UserFeedbackException("MiMo 流式响应过大。");
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw new UserFeedbackException("MiMo 未返回 SSE 流式音频。");
                using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                byte? carry = null; long segmentBytes = 0;
                await foreach (var chunk in ReadSseAudioAsync(input, cancellation, metrics: metrics).ConfigureAwait(false))
                {
                    if (metrics is not null) metrics.FirstAudioMilliseconds ??= metrics.ElapsedMilliseconds;
                    cancellation.ThrowIfCancellationRequested(); audioBytes += chunk.Length; segmentBytes += chunk.Length;
                    if (metrics is not null) metrics.AudioBytes = audioBytes;
                    if (audioBytes > HttpSpeech.MaxAudioBytes) throw new UserFeedbackException("MiMo 流式音频过大，请分次朗读。");
                    var writeClock = Stopwatch.StartNew(); var start = 0;
                    if (carry is { } previous)
                    {
                        // Join just the split sample, never copy an entire large event for one carry byte.
                        await buffer.WriteAsync(new byte[] { previous, chunk[0] }, cancellation).ConfigureAwait(false);
                        start = 1; carry = null;
                    }
                    if ((chunk.Length - start) % 2 != 0) carry = chunk[^1];
                    await buffer.WriteAsync(chunk.AsMemory(start, (chunk.Length - start) & ~1), cancellation).ConfigureAwait(false);
                    if (metrics is not null) metrics.BufferWriteMilliseconds += writeClock.Elapsed.TotalMilliseconds;
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

    // One bounded UTF-8 frame; the usual single data line is parsed in place.
    // Only multi-data-line events need a second UTF-8 buffer. No whole-event UTF-16 copies.
    internal static async IAsyncEnumerable<byte[]> ReadSseAudioAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellation,
        TimeSpan? idleTimeout = null, MiMoStreamingMetrics? metrics = null)
    {
        var block = new byte[8192]; using var frame = new MemoryStream(); using var combined = new MemoryStream();
        var utf8 = new UTF8Encoding(false, true); long received = 0;
        var afterCr = false; var eventBytes = 0; var lineStart = 0; var firstDataStart = -1; var firstDataLength = 0; var multipleData = false;
        var activeRead = TimeSpan.Zero; var audioSeen = false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            budget.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(45));
            var clock = Stopwatch.StartNew(); int read;
            try { read = await input.ReadAsync(block, budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                var stage = audioSeen ? "网络空闲" : "首次音频接收";
                if (metrics is not null) metrics.TimeoutStage = stage;
                throw new UserFeedbackException($"MiMo {stage}超时，已停止，未重试已播放内容。");
            }
            activeRead += clock.Elapsed;
            if (metrics is not null) metrics.NetworkReadMilliseconds += clock.Elapsed.TotalMilliseconds;
            if (activeRead > TimeSpan.FromMinutes(10))
            {
                if (metrics is not null) metrics.TimeoutStage = "累计网络接收";
                throw new UserFeedbackException("MiMo 流式累计网络接收耗时超限。");
            }
            if (read == 0) throw new UserFeedbackException("MiMo SSE 未收到正常完成标记，流已中断。");
            if (metrics is not null) metrics.FirstBodyByteMilliseconds ??= metrics.ElapsedMilliseconds;
            received += read;
            if (received > HttpSpeech.MaxResponseBytes) throw new UserFeedbackException("MiMo SSE 响应过大。");
            for (var i = 0; i < read; ++i)
            {
                cancellation.ThrowIfCancellationRequested(); var ch = block[i];
                if (afterCr && ch == 10)
                {
                    // Count CRLF's second wire byte, except the empty dispatch line's delimiter.
                    if (frame.Length > 0) CheckEventSize(++eventBytes, metrics);
                    afterCr = false; continue;
                }
                afterCr = false;
                if (ch is not (13 or 10))
                {
                    CheckEventSize(++eventBytes, metrics);
                    frame.WriteByte(ch); continue;
                }
                afterCr = ch == 13;
                var lineLength = (int)frame.Length - lineStart;
                if (lineLength == 0)
                {
                    if (metrics is not null) metrics.MaxEventBytes = Math.Max(metrics.MaxEventBytes, eventBytes);
                    var payload = multipleData ? combined.GetBuffer().AsMemory(0, (int)combined.Length) :
                        firstDataStart >= 0 ? frame.GetBuffer().AsMemory(firstDataStart, firstDataLength) : ReadOnlyMemory<byte>.Empty;
                    payload = TrimPayload(payload);
                    if (payload.Span.SequenceEqual("[DONE]"u8)) yield break;
                    byte[]? pcm = null;
                    if (payload.Length > 0) pcm = ParseAudioEvent(payload, cancellation, metrics);
                    frame.SetLength(0); combined.SetLength(0); eventBytes = 0; lineStart = 0; firstDataStart = -1; firstDataLength = 0; multipleData = false;
                    if (pcm is { Length: > 0 }) { audioSeen = true; yield return pcm; }
                }
                else
                {
                    // Strict UTF-8 validation also applies to ignored metadata, without allocating a string.
                    utf8.GetCharCount(frame.GetBuffer(), lineStart, lineLength);
                    if (IsDataLine(frame.GetBuffer(), lineStart, lineLength, out var valueStart, out var valueLength))
                    {
                        if (firstDataStart < 0) { firstDataStart = valueStart; firstDataLength = valueLength; }
                        else
                        {
                            if (!multipleData) { combined.Write(frame.GetBuffer(), firstDataStart, firstDataLength); multipleData = true; }
                            combined.WriteByte(10); combined.Write(frame.GetBuffer(), valueStart, valueLength);
                        }
                    }
                    CheckEventSize(++eventBytes, metrics);
                    frame.WriteByte(10); lineStart = (int)frame.Length;
                }
            }
        }
    }

    private static void CheckEventSize(long bytes, MiMoStreamingMetrics? metrics)
    {
        if (bytes <= MaxSseEventBytes) return;
        if (metrics is not null) metrics.MaxEventBytes = (int)bytes;
        throw new UserFeedbackException("MiMo SSE 事件超过 16 MiB 安全上限，请缩短文本后重试。");
    }
    private static ReadOnlyMemory<byte> TrimPayload(ReadOnlyMemory<byte> payload)
    {
        var start = 0; var end = payload.Length;
        while (start < end && payload.Span[start] is 9 or 10 or 13 or 32) ++start;
        while (end > start && payload.Span[end - 1] is 9 or 10 or 13 or 32) --end;
        return payload[start..end];
    }
    private static bool IsDataLine(byte[] bytes, int start, int length, out int valueStart, out int valueLength)
    {
        var line = bytes.AsSpan(start, length); valueStart = start + length; valueLength = 0;
        if (line.SequenceEqual("data"u8)) return true;
        if (!line.StartsWith("data:"u8)) return false;
        valueStart = start + 5;
        if (length > 5 && bytes[valueStart] == 32) ++valueStart;
        valueLength = start + length - valueStart; return true;
    }
    private static byte[]? ParseAudioEvent(ReadOnlyMemory<byte> payload, CancellationToken cancellation, MiMoStreamingMetrics? metrics)
    {
        cancellation.ThrowIfCancellationRequested(); var clock = Stopwatch.StartNew();
        var eventCompletedMilliseconds = metrics?.ElapsedMilliseconds;
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); var root = json.RootElement;
            cancellation.ThrowIfCancellationRequested();
            if (root.TryGetProperty("error", out _)) throw new UserFeedbackException("MiMo 服务报告流式错误，已停止朗读。");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                throw new UserFeedbackException("MiMo SSE 响应缺少 choices。");
            if (choices.GetArrayLength() == 0) return null;
            if (choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null &&
                (finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop"))
                throw new UserFeedbackException("MiMo 合成未正常结束，已停止接收后续音频。");
            if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind == JsonValueKind.Null) return null;
            if (!delta.TryGetProperty("audio", out var audio) || audio.ValueKind == JsonValueKind.Null) return null;
            if (!audio.TryGetProperty("data", out var encoded)) throw new UserFeedbackException("MiMo SSE 音频块缺少数据。");
            var base64 = encoded.GetString();
            if (string.IsNullOrWhiteSpace(base64)) return null;
            if (metrics is not null) metrics.FirstCompleteAudioEventMilliseconds ??= eventCompletedMilliseconds;
            var pcm = DecodeBase64(base64, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return pcm;
        }
        finally { if (metrics is not null) metrics.ParseMilliseconds += clock.Elapsed.TotalMilliseconds; }
    }

}

// Opt-in diagnostics contain timing/counts only, never text, credentials or HTTP bodies.
internal sealed class MiMoStreamingMetrics
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    internal double ElapsedMilliseconds => clock.Elapsed.TotalMilliseconds;
    internal double? ResponseHeadersMilliseconds, FirstBodyByteMilliseconds, FirstCompleteAudioEventMilliseconds, FirstAudioMilliseconds, NetworkCompleteMilliseconds;
    internal double NetworkReadMilliseconds, ParseMilliseconds, BufferWriteMilliseconds;
    internal long AudioBytes;
    internal int MaxEventBytes;
    internal string? TimeoutStage;
    internal int Requests;
}
