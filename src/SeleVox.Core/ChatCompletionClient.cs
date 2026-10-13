using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SeleVox.Core;

// No retry and no redirect: one explicit action makes at most one billable request.
public sealed class ChatCompletionClient
{
    public const int MaxText = 32768, MaxEventBytes = 262144, MaxResponseBytes = 4194304;
    private static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    private readonly TimeSpan idleTimeout, totalTimeout;
    public ChatCompletionClient(HttpClient? client = null, TimeSpan? idleTimeout = null, TimeSpan? totalTimeout = null)
    {
        this.client = client ?? Shared;
        this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(45);
        this.totalTimeout = totalTimeout ?? TimeSpan.FromMinutes(3);
    }
    public static void Validate(string question, Settings settings, string? key)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaxText)
            throw new UserFeedbackException("问题为空或超过 32768 字符，请缩短后重试。");
        if (!settings.HasValidLlmConfiguration) throw new UserFeedbackException("请先保存合法的 AI Base URL、Model 和 System Prompt（最多 8192 字符）。");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1280 || key.Any(char.IsControl))
            throw new UserFeedbackException("请为当前 AI 服务地址保存有效的 API Key。");
    }
    public async Task<string> GenerateAsync(string question, Settings settings, string key, Action<string> delta, CancellationToken cancellation)
    {
        Validate(question, settings, key);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(totalTimeout);
        var token = budget.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Settings.NormalizeServiceUrl(settings.LlmBaseUrl) + "/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = settings.LlmModel, stream = settings.LlmStreaming,
                messages = new[] { new { role = "system", content = settings.LlmSystemPrompt }, new { role = "user", content = question } }
            }), Encoding.UTF8, "application/json");
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(token); headers.CancelAfter(idleTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            headers.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!response.IsSuccessStatusCode)
                throw new UserFeedbackException($"AI 服务返回 HTTP {(int)response.StatusCode}。请检查地址、模型、凭据或额度；未自动重试。流式不兼容时可关闭 Streaming 后主动重试。");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw Limit();
            using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var bytes = new MemoryStream(); var buffer = new byte[8192];
                int count;
                while ((count = await ReadAsync(body, buffer, token).ConfigureAwait(false)) != 0)
                { if (bytes.Length + count > MaxResponseBytes) throw Limit(); bytes.Write(buffer, 0, count); }
                using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var choice = json.RootElement.GetProperty("choices")[0];
                CheckFinish(choice.GetProperty("finish_reason").GetString());
                var text = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
                CheckText(text); delta(text); return text;
            }
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new UserFeedbackException("AI 响应格式不兼容；请选择非流式模式后主动重试。未重复提交。");
            var answer = new StringBuilder(); var data = new StringBuilder();
            using var line = new MemoryStream(); var chunk = new byte[8192];
            int total = 0, eventSize = 0; bool afterCr = false, finished = false, firstLine = true;
            var utf8 = new UTF8Encoding(false, true);
            bool Dispatch()
            {
                if (data.Length == 0) return false;
                var payload = data.ToString().TrimEnd('\n'); data.Clear();
                if (payload == "[DONE]")
                {
                    if (!finished) throw new UserFeedbackException("AI 流缺少正常结束原因，回答不完整；未自动朗读。");
                    CheckText(answer.ToString()); return true;
                }
                using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.TryGetProperty("error", out _)) throw new UserFeedbackException("AI 服务在生成途中报告错误；未自动重试或朗读。");
                foreach (var choice in json.RootElement.GetProperty("choices").EnumerateArray())
                {
                    if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) continue;
                    if (choice.TryGetProperty("delta", out var update) && update.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                    {
                        var text = content.GetString() ?? "";
                        if (text.Length != 0)
                        {
                            if (finished) throw new UserFeedbackException("AI 流在结束后仍返回正文。");
                            if (answer.Length + text.Length > MaxText) throw Limit();
                            answer.Append(text); delta(text);
                        }
                    }
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                    { CheckFinish(reason.GetString()); finished = true; }
                }
                return false;
            }
            bool EndLine()
            {
                var text = utf8.GetString(line.GetBuffer(), 0, (int)line.Length); line.SetLength(0);
                if (firstLine) { text = text.TrimStart('\uFEFF'); firstLine = false; }
                if (text.Length == 0) { var done = Dispatch(); eventSize = 0; return done; }
                if (text.StartsWith("data:", StringComparison.Ordinal))
                { var value = text[5..]; if (value.StartsWith(' ')) value = value[1..]; data.Append(value).Append('\n'); }
                return false;
            }
            int read;
            while ((read = await ReadAsync(body, chunk, token).ConfigureAwait(false)) != 0)
            {
                total = checked(total + read); if (total > MaxResponseBytes) throw Limit();
                for (var i = 0; i < read; ++i)
                {
                    token.ThrowIfCancellationRequested(); var b = chunk[i];
                    if (++eventSize > MaxEventBytes) throw Limit();
                    if (afterCr && b == 10) { afterCr = false; continue; }
                    afterCr = b == 13;
                    if (b is 10 or 13) { if (EndLine()) return answer.ToString(); }
                    else line.WriteByte(b);
                }
            }
            throw new UserFeedbackException("AI 流在 [DONE] 前断开，已保留部分回答；未自动朗读或重试。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new UserFeedbackException("AI 网络等待超时；未自动重试或朗读。"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or DecoderFallbackException)
        { throw new UserFeedbackException("AI 响应格式无效或不完整；未自动重试或朗读。"); }
        catch (HttpRequestException) { throw new UserFeedbackException("无法连接 AI 服务或连接中断；未自动重试或朗读。"); }
        catch (IOException) { throw new UserFeedbackException("AI 响应读取中断；未自动重试或朗读。"); }
    }
    private async Task<int> ReadAsync(Stream body, byte[] buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(idleTimeout);
        return await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
    }
    private static void CheckFinish(string? reason)
    {
        if (reason != "stop") throw new UserFeedbackException("AI 回答未正常完成（长度限制、过滤或不支持的结束类型）；未自动朗读。");
    }
    private static void CheckText(string text)
    { if (string.IsNullOrWhiteSpace(text)) throw new UserFeedbackException("AI 返回空回答。"); if (text.Length > MaxText) throw Limit(); }
    private static UserFeedbackException Limit() => new("AI 响应超过安全大小上限；未自动重试或朗读。");
}
