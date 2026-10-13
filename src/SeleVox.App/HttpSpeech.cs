using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SeleVox.Core;

namespace SeleVox.App;

internal sealed partial class MiMoSpeech(HttpClient? client = null, Func<string?>? readKey = null)
{
    public async Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
        var style = GetStyle(settings);
        // 8K context: conservatively bound UTF-8 bytes (including up to 3K style bytes),
        // leaving room for message framing. No approximate tokenizer or source rewriting.
        return await HttpSpeech.SynthesizeAsync(client, readKey ?? AzureCredentials.MiMo().Read,
            new Uri("https://api.xiaomimimo.com/v1/chat/completions"), text, 3000, true,
            part => CreateRequest(part, settings.MiMoVoice, style, settings.MiMoModel), Decode, "MiMo", cancellation).ConfigureAwait(false);
    }
    private static string GetStyle(Settings settings)
    {
        if (!double.IsFinite(settings.Rate) || settings.Rate is < .5 or > 2 || !Settings.MiMoVoices.Contains(settings.MiMoVoice) ||
            settings.MiMoStyle is not { Length: <= 1000 } || string.IsNullOrWhiteSpace(settings.MiMoModel) || settings.MiMoModel.Length > 200)
            throw new UserFeedbackException("请检查 MiMo 模型名称、预置音色和风格说明（最多 1000 字符）。");
        var style = settings.MiMoStyle;
        if (settings.Rate != 1) style += (style.Length > 0 ? "\n" : "") +
            (settings.Rate < 1 ? "请放慢朗读速度。" : "请加快朗读速度。") +
            $"目标约为正常语速的 {settings.Rate.ToString("0.##", CultureInfo.InvariantCulture)} 倍。";
        return style;
    }

    internal static object CreateRequest(string text, string voice, string style, string model, bool streaming = false)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(style)) messages.Add(new { role = "user", content = style });
        messages.Add(new { role = "assistant", content = text });
        return new { model, stream = streaming, messages, audio = new { format = streaming ? "pcm16" : "wav", voice } };
    }

    internal static byte[] Decode(byte[] body, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var data = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("audio").GetProperty("data").GetString();
            if (string.IsNullOrWhiteSpace(data)) throw new UserFeedbackException("MiMo 响应缺少非空音频。");
            var bytes = DecodeBase64(data, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (FormatException) { throw new UserFeedbackException("MiMo 音频 Base64 无效。"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        { throw new UserFeedbackException("MiMo 响应 JSON 无效或缺少音频。"); }
    }

    internal static byte[] DecodeBase64(string data, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var chars = new char[65536]; var decoded = new byte[49152]; var count = 0; var padding = 0;
        void Flush()
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Convert.TryFromBase64Chars(chars.AsSpan(0, count), decoded, out var written)) throw new FormatException();
            output.Write(decoded, 0, written); count = 0;
        }
        for (var i = 0; i < data.Length; ++i)
        {
            if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
            var ch = data[i];
            if (ch is ' ' or '\t' or '\r' or '\n') continue;
            if (ch == '=') { if (++padding > 2) throw new FormatException(); }
            else if (padding > 0) throw new FormatException();
            chars[count++] = ch;
            if (count == chars.Length) Flush();
        }
        if (count > 0) Flush();
        cancellation.ThrowIfCancellationRequested();
        return output.ToArray();
    }
}

internal sealed class OpenAiSpeech(HttpClient? client = null, Func<string?>? readKey = null)
{
    public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
    {
        var address = Settings.NormalizeServiceUrl(settings.OpenAiBaseUrl) ??
            throw new UserFeedbackException("服务地址无效：远程服务需要 HTTPS，本机服务可使用回环 HTTP 地址。");
        if (!double.IsFinite(settings.Rate) || settings.Rate is < .5 or > 2 ||
            string.IsNullOrWhiteSpace(settings.OpenAiModel) || string.IsNullOrWhiteSpace(settings.OpenAiVoice) ||
            settings.OpenAiInstructions is not { Length: <= 4096 }) throw new UserFeedbackException("请检查 Model、Voice 和 Instructions。");
        if (settings.OpenAiModel is "tts-1" or "tts-1-hd" && !string.IsNullOrWhiteSpace(settings.OpenAiInstructions))
            throw new UserFeedbackException("tts-1 / tts-1-hd 不支持 Instructions，请清空后保存或选择支持的模型。");
        return HttpSpeech.SynthesizeAsync(client, readKey ?? AzureCredentials.OpenAi(address).Read,
            new Uri(address + "/audio/speech"), text, 4096, false,
            part => CreateRequest(part, settings), FinalizeWave,
            "OpenAI-Compatible", cancellation);
    }

    internal static byte[] FinalizeWave(byte[] body, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Some speech endpoints emit streaming RIFF/data length sentinels even when
        // consumed as one binary response. Only after successful bounded HTTP EOF,
        // finalize BOTH unknown lengths; never repair ordinary declared truncation.
        if (body.Length >= 44 && Encoding.ASCII.GetString(body, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(body, 8, 4) == "WAVE" && BitConverter.ToUInt32(body, 4) == uint.MaxValue)
        {
            var cursor = 12; var fmtFound = false;
            while (cursor <= body.Length - 8)
            {
                cancellation.ThrowIfCancellationRequested();
                var chunk = Encoding.ASCII.GetString(body, cursor, 4); var size = BitConverter.ToUInt32(body, cursor + 4);
                if (chunk == "data")
                {
                    if (fmtFound && size == uint.MaxValue)
                    {
                        BitConverter.TryWriteBytes(body.AsSpan(4, 4), (uint)(body.Length - 8));
                        BitConverter.TryWriteBytes(body.AsSpan(cursor + 4, 4), (uint)(body.Length - cursor - 8));
                    }
                    break;
                }
                var next = (long)cursor + 8 + size + (size & 1);
                if (next > body.Length) break;
                if (chunk == "fmt " && size >= 16) fmtFound = true;
                cursor = (int)next;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return body;
    }

    internal static object CreateRequest(string text, Settings settings)
    {
        var fields = new Dictionary<string, object> { ["model"] = settings.OpenAiModel, ["input"] = text,
            ["voice"] = settings.OpenAiVoice, ["response_format"] = "wav", ["speed"] = settings.Rate };
        if (!string.IsNullOrWhiteSpace(settings.OpenAiInstructions)) fields["instructions"] = settings.OpenAiInstructions;
        return fields;
    }
}

internal static class HttpSpeech
{
    internal const int MaxResponseBytes = 48 * 1024 * 1024, MaxAudioBytes = 64 * 1024 * 1024, MaxTextLength = 32768;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static IEnumerable<string> SplitText(string text, int limit, bool utf8, CancellationToken cancellation = default)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var start = 0; var count = 0; var preferred = -1; var index = 0;
        while (index < boundaries.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var end = index + 1 < boundaries.Length ? boundaries[index + 1] : text.Length;
            var size = utf8 ? Encoding.UTF8.GetByteCount(text.AsSpan(boundaries[index], end - boundaries[index])) : end - boundaries[index];
            if (size > limit) throw new UserFeedbackException("文本包含超过单次限制的 Unicode 字符组合，请缩短后重试。");
            if (count + size > limit)
            {
                var cut = preferred > start ? preferred : boundaries[index];
                yield return text[start..cut];
                start = cut; count = 0; preferred = -1;
                index = Array.BinarySearch(boundaries, start);
                continue;
            }
            count += size;
            if ("\n\r。！？.!?;；".Contains(text[end - 1])) preferred = end;
            ++index;
        }
        if (start < text.Length) yield return text[start..];
    }

    internal static async Task<Stream> SynthesizeAsync(HttpClient? client, Func<string?> credential, Uri endpoint,
        string text, int limit, bool utf8, Func<string, object> payload, Func<byte[], CancellationToken, byte[]> decode,
        string label, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new UserFeedbackException($"文本为空或超过 {MaxTextLength} 字符，请分次朗读。");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        session.CancelAfter(TimeSpan.FromMinutes(10));
        using var merged = new MemoryStream();
        WaveFileWriter? writer = null; WaveFormat? format = null;
        try
        {
            var key = credential();
            if (string.IsNullOrWhiteSpace(key)) throw new UserFeedbackException($"请先保存 {label} API Key（兼容服务 Key 按服务地址独立保存）。");
            foreach (var part in SplitText(text, limit, utf8, session.Token))
            {
                using var segment = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                segment.CancelAfter(TimeSpan.FromSeconds(90));
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(JsonSerializer.Serialize(payload(part)), Encoding.UTF8, "application/json");
                using var response = await (client ?? SharedClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, segment.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new UserFeedbackException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{label} 认证失败，请检查 API Key 和服务权限。",
                    HttpStatusCode.TooManyRequests => $"{label} 请求过多或额度不足，请稍后重试。",
                    HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => $"{label} 拒绝请求，请检查模型、音色、Instructions 等参数或缩短文本。",
                    _ => $"{label} 服务错误（HTTP {(int)response.StatusCode}），不会重定向或切换服务。"
                });
                var body = await ReadBoundedAsync(response.Content, segment.Token).ConfigureAwait(false);
                var bytes = decode(body, segment.Token);
                segment.Token.ThrowIfCancellationRequested();
                using var audio = ValidatedWave(bytes, segment.Token);
                if (format is null) { format = audio.WaveFormat; writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(merged), format); }
                else if (!format.Equals(audio.WaveFormat)) throw new UserFeedbackException("分段音频格式不一致，请更换模型或服务后重试。");
                if (merged.Length + audio.Length > MaxAudioBytes) throw new UserFeedbackException("合成音频过大，请分次朗读。");
                var buffer = new byte[16384]; int read;
                while ((read = audio.Read(buffer, 0, buffer.Length)) > 0)
                { segment.Token.ThrowIfCancellationRequested(); writer!.Write(buffer, 0, read); }
            }
            writer?.Dispose(); writer = null;
            session.Token.ThrowIfCancellationRequested();
            var result = merged.ToArray(); session.Token.ThrowIfCancellationRequested();
            return new MemoryStream(result, writable: false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new UserFeedbackException($"{label} 请求超时，请缩短文本或稍后重试。"); }
        catch (HttpRequestException) { throw new UserFeedbackException($"无法连接 {label}，请检查网络和服务地址。"); }
        catch (IOException) { throw new UserFeedbackException($"{label} 音频响应读取失败或格式无效。"); }
        catch (ArgumentException) { throw new UserFeedbackException($"{label} 请求参数或音频格式无效，请检查设置。"); }
        catch (FormatException) { throw new UserFeedbackException($"{label} API Key 或请求格式无效，请检查设置。"); }
        finally { writer?.Dispose(); }
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new UserFeedbackException("语音响应过大，请缩短文本。");
        using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxResponseBytes) throw new UserFeedbackException("语音响应过大，请缩短文本。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    internal static WaveFileReader ValidatedWave(byte[] bytes, CancellationToken cancellation = default)
    {
        WaveFileReader? reader = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" || (long)BitConverter.ToUInt32(bytes, 4) + 8 != bytes.Length) throw new FormatException();
            var cursor = 12; var dataFound = false; var fmtFound = false;
            while (cursor < bytes.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                if (cursor > bytes.Length - 8) throw new FormatException();
                var size = BitConverter.ToUInt32(bytes, cursor + 4);
                var next = (long)cursor + 8 + size + (size & 1);
                if (next > bytes.Length) throw new FormatException();
                var chunk = Encoding.ASCII.GetString(bytes, cursor, 4);
                if (chunk == "data") { if (dataFound || size == 0) throw new FormatException(); dataFound = true; }
                if (chunk == "fmt ") { if (fmtFound || size < 16) throw new FormatException(); fmtFound = true; }
                cursor = (int)next;
            }
            if (!dataFound || !fmtFound) throw new FormatException();
            reader = new WaveFileReader(new MemoryStream(bytes, writable: false));
            var f = reader.WaveFormat;
            if (f.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) ||
                f.Channels is < 1 or > 8 || f.SampleRate is < 8000 or > 192000 ||
                (f.Encoding == WaveFormatEncoding.Pcm ? f.BitsPerSample is not (8 or 16 or 24 or 32) : f.BitsPerSample != 32) ||
                f.BlockAlign != f.Channels * (f.BitsPerSample / 8) || f.AverageBytesPerSecond != f.SampleRate * f.BlockAlign ||
                reader.Length <= 0 || reader.Length % f.BlockAlign != 0) throw new FormatException();
            return reader;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or DivideByZeroException or NotSupportedException)
        { reader?.Dispose(); throw new UserFeedbackException("服务未返回合法、完整、非空的 PCM/IEEE Float WAVE 音频。"); }
    }

}
