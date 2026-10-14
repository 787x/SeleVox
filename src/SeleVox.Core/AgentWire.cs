using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace SeleVox.Core;
public static class AgentWire
{
    public const int MaxMessageBytes = 1024 * 1024;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string PipeName { get { using var identity = WindowsIdentity.GetCurrent(); return "SeleVox-Agent-v1-" + identity.User!.Value; } }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AgentJson.Options);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException();
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaxMessageBytes) throw new InvalidDataException();
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, AgentJson.Options) ?? throw new InvalidDataException();
    }
}
