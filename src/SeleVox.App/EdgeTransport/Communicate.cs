// Adapted from EdgeTTS.DotNet v0.4.0 (MIT). See NOTICE.md.
namespace SeleVox.App.EdgeTransport;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using EdgeTTS.DotNet;
using EdgeTTS.DotNet.Models;




internal sealed class Communicate
{
    private readonly TTSConfig _config;
    private readonly IEnumerable<string> _textParts;

    public Communicate(string text, string? voice = null, string rate = "+0%", string volume = "+0%", string pitch = "+0Hz", string boundaryType = "SentenceBoundary")
    {
        _config = new TTSConfig(voice, rate, volume, pitch, boundaryType);

        // Clean text, escape, and then split
        var cleanedText = RemoveIncompatibleCharacters(text);
        var escapedText = WebUtility.HtmlEncode(cleanedText);
        _textParts = new[] { escapedText };
    }

    private static string RemoveIncompatibleCharacters(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            int code = chars[i];
            if ((code >= 0 && code <= 8) || (code >= 11 && code <= 12) || (code >= 14 && code <= 31))
            {
                chars[i] = ' ';
            }
        }
        return new string(chars);
    }

    
    /// <summary>
    /// Streams audio and metadata from the service.
    /// </summary>
    /// <returns>An asynchronous stream of TTSChunk objects.</returns>
    public async IAsyncEnumerable<TTSChunk> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var offsetCompensation = TimeSpan.Zero;
        long cumulativeAudioBytes = 0;
        var lastDurationOffset = TimeSpan.Zero;

        foreach (var part in _textParts)
        {
            long chunkAudioBytes = 0;
            if (cancellationToken.IsCancellationRequested) break;

            // No identity rotation, clock correction or retry on service refusal.
            // Scope includes ConnectAsync so failed handshakes also release the socket.
            var connectId = Guid.NewGuid().ToString("N");
            var wssUrl = $"{Constants.WssUrl}&ConnectionId={connectId}&Sec-MS-GEC={Drm.GenerateSecMsGec()}&Sec-MS-GEC-Version={Constants.SecMsGecVersion}";
            using var webSocket = await ConnectAsync(new Uri(wssUrl), cancellationToken);
            var completed = false;
            var wordBoundaryEnabled = _config.BoundaryType == "WordBoundary" ? "true" : "false";
            var sentenceBoundaryEnabled = _config.BoundaryType == "SentenceBoundary" ? "true" : "false";

            var speechConfig = "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"" + sentenceBoundaryEnabled + "\",\"wordBoundaryEnabled\":\"" + wordBoundaryEnabled + "\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
            var speechConfigMessage = $"X-Timestamp:{GetDateString()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n{speechConfig}\r\n";
            await SendMessageAsync(webSocket, speechConfigMessage, cancellationToken);

            // Send SSML
            var ssml = CreateSsml(part);
            var requestId = Guid.NewGuid().ToString("N");
            var ssmlMessage = $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{GetDateString()}Z\r\nPath:ssml\r\n\r\n{ssml}";
            await SendMessageAsync(webSocket, ssmlMessage, cancellationToken);

            var audioWasReceivedInThisTurn = false;
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var (headers, data) = await ReceiveMessageAsync(webSocket, cancellationToken);
                if (headers is null)
                {
                    break; 
                }

                if (headers.TryGetValue("Path", out var path))
                {
                    if (path == "audio.metadata")
                    {
                        var metadata = ParseMetadata(data, offsetCompensation);
                        if (metadata != null)
                        {
                            lastDurationOffset = metadata.Offset + metadata.Duration;
                            yield return metadata;
                        }
                    }
                    else if (path == "turn.end")
                    {
                        cumulativeAudioBytes += chunkAudioBytes;
                        long ticks = (cumulativeAudioBytes * 8 * Constants.TicksPerSecond) / Constants.Mp3BitrateBps;
                        offsetCompensation = TimeSpan.FromTicks(ticks);
                        chunkAudioBytes = 0;
                        
                        completed = true;
                        yield return new TurnEndChunk();
                        break;
                    }
                    else if (path == "audio")
                    {
                        headers.TryGetValue("Content-Type", out var contentType);

                        if (string.IsNullOrEmpty(contentType))
                        {
                            if (data.Length == 0)
                            {
                                continue;
                            }
                        }
                        else if (!contentType.StartsWith("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        audioWasReceivedInThisTurn = true;
                        chunkAudioBytes += data.Length;
                        yield return new AudioChunk(data);
                    }
                    else if (path != "response" && path != "turn.start")
                    {
                        throw new UnknownResponseException($"Unknown path received: {path}");
                    }
                }
                else
                {
                    if (headers.TryGetValue("Content-Type", out var contentType) && contentType.StartsWith("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        audioWasReceivedInThisTurn = true;
                        chunkAudioBytes += data.Length;
                        yield return new AudioChunk(data);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!audioWasReceivedInThisTurn)
                throw new NoAudioReceivedException("No audio received.");
            if (!completed)
                throw new UnexpectedResponseException("Turn ended without completion.");

        }
    }
    
    private string CreateSsml(string text)
    {
        return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
               $"<voice name='{System.Security.SecurityElement.Escape(_config.Voice)}'>" +
               $"<prosody pitch='{_config.Pitch}' rate='{_config.Rate}' volume='{_config.Volume}'>" +
               text +
               "</prosody>" +
               "</voice>" +
               "</speak>";
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancellation)
    {
        // One retry only for a transport failure BEFORE any text/audio is sent.
        // URI, connection ID and headers stay identical; refusal never retries.
        for (var attempt = 0; ; ++attempt)
        {
            var socket = new ClientWebSocket(); socket.Options.CollectHttpResponseDetails = true;
            foreach (var header in Constants.WssHeaders) socket.Options.SetRequestHeader(header.Key, header.Value);
            try { await socket.ConnectAsync(uri, cancellation); return socket; }
            catch (System.Net.WebSockets.WebSocketException) when ((int)socket.HttpStatusCode >= 400)
            {
                var status = socket.HttpStatusCode; socket.Dispose();
                throw new System.Net.Http.HttpRequestException("Edge handshake rejected.", null, status);
            }
            catch (System.Net.WebSockets.WebSocketException) when (attempt == 0 && !cancellation.IsCancellationRequested)
            { socket.Dispose(); await Task.Delay(500, cancellation); }
            catch { socket.Dispose(); throw; }
        }
    }

    private static string GetDateString()
    {
        return DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SendMessageAsync(ClientWebSocket webSocket, string message, CancellationToken cancellationToken)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<(Dictionary<string, string>? headers, byte[] data)> ReceiveMessageAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        var buffer = new byte[1024 * 8]; // 8KB buffer
        WebSocketReceiveResult result;

        try
        {
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (null, Array.Empty<byte>());
                }
                memoryStream.Write(buffer, 0, result.Count);
                if (memoryStream.Length > 1024 * 1024) throw new UnexpectedResponseException("Oversized service message.");
            } while (!result.EndOfMessage);
        }
        catch (System.Net.WebSockets.WebSocketException ex)
        {
            throw new EdgeTTS.DotNet.WebSocketException("WebSocket receive error", ex);
        }

        memoryStream.Seek(0, SeekOrigin.Begin);
        var messageBytes = memoryStream.ToArray();

        if (result.MessageType == WebSocketMessageType.Binary)
        {
            if (messageBytes.Length < 2)
            {
                throw new UnexpectedResponseException("Binary message too short to contain header length.");
            }

            var headerLength = (messageBytes[0] << 8) | messageBytes[1]; // Big-endian

            if (headerLength > messageBytes.Length - 2)
            {
                throw new UnexpectedResponseException($"Header length ({headerLength}) is greater than remaining data length ({messageBytes.Length - 2}).");
            }

            var headerData = messageBytes.AsSpan(2, headerLength);
            var bodyData = messageBytes.AsSpan(2 + headerLength).ToArray();

            var headersStr = Encoding.UTF8.GetString(headerData);
            var headers = ParseHeaders(headersStr);

            return (headers, bodyData);
        }
        else if (result.MessageType == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(messageBytes);
            var separator = "\r\n\r\n";
            var separatorIndex = text.IndexOf(separator, StringComparison.Ordinal);

            if (separatorIndex == -1)
            {
                return (new Dictionary<string, string>(), messageBytes);
            }

            var headersStr = text.Substring(0, separatorIndex);
            var bodyStr = text.Substring(separatorIndex + separator.Length);

            var headers = ParseHeaders(headersStr);
            var bodyData = Encoding.UTF8.GetBytes(bodyStr);

            return (headers, bodyData);
        }

        throw new UnknownResponseException($"Received unhandled message type: {result.MessageType}");
    }

    private static Dictionary<string, string> ParseHeaders(string headersStr)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = headersStr.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
            {
                headers[parts[0].Trim()] = parts[1].Trim();
            }
        }
        return headers;
    }

    private static MetadataChunk? ParseMetadata(byte[] data, TimeSpan offsetCompensation)
    {
        try
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(data));
            var metadataArray = json?["Metadata"]?.AsArray();
            if (metadataArray == null) return null;

            foreach (var metaObj in metadataArray)
            {
                var type = metaObj?["Type"]?.GetValue<string>();
                if (type == "WordBoundary" || type == "SentenceBoundary")
                {
                    var offsetTicks = metaObj?["Data"]?["Offset"]?.GetValue<long>() ?? 0;
                    var durationTicks = metaObj?["Data"]?["Duration"]?.GetValue<long>() ?? 0;
                    var text = metaObj?["Data"]?["text"]?["Text"]?.GetValue<string>() ?? "";

                    var offset = TimeSpan.FromTicks(offsetTicks) + offsetCompensation;
                    var duration = TimeSpan.FromTicks(durationTicks);

                    return new MetadataChunk(type, offset, duration, WebUtility.HtmlDecode(text));
                }
                else if (type == "SessionEnd")
                {
                    continue;
                }
                else
                {
                    throw new UnknownResponseException($"Unknown metadata type: {type}");
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (UnknownResponseException)
        {
            throw;
        }
        catch (Exception)
        {
        }
        return null;
    }
}
