using System.IO.Pipes;
using SeleVox.Core;
namespace SeleVox.App;
internal sealed class AgentPipe : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task[] workers;
    internal AgentPipe(AgentExecution execution, string? pipeName = null)
    {
        workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName ?? AgentWire.PipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    AgentPipePeer.ValidateLocal(pipe);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    AgentRequest request;
                    try { request = await AgentWire.ReadAsync<AgentRequest>(pipe, timeout.Token); }
                    catch (Exception ex) { await AgentWire.WriteAsync(pipe, Failure(ex), timeout.Token); continue; }
                    timeout.CancelAfter(TimeSpan.FromMinutes(20));
                    using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var monitor = MonitorAsync(pipe, disconnected);
                    try
                    {
                        AgentReply reply;
                        try { reply = new(true, "ok", AgentJson.Element(await execution.ExecuteAsync(request, disconnected.Token))); }
                        catch (Exception ex) { reply = Failure(ex); }
                        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(reply, AgentJson.Options).Length > AgentWire.MaxMessageBytes)
                            reply = new(false, "result_too_large", AgentJson.Empty);
                        try { await AgentWire.WriteAsync(pipe, reply, timeout.Token); }
                        catch
                        {
                            if (reply.Success && request.Command is "speak" or "synthesize" && reply.Data.TryGetProperty("Id", out var unacknowledged))
                                execution.CancelJob(unacknowledged.GetString()!);
                            throw;
                        }
                        // An MCP job has a live connection lease after submission. Closing the MCP process
                        // closes this pipe, cancelling that job even after its tool call has returned.
                        if (reply.Success && request.Command is "speak" or "synthesize" &&
                            request.Arguments.TryGetProperty("mcp", out var mcp) && mcp.GetBoolean() &&
                            reply.Data.TryGetProperty("Id", out var id))
                            await execution.WaitForJobAsync(id.GetString()!, disconnected.Token);
                    }
                    finally { disconnected.Cancel(); await monitor; }
                }
                catch (Exception) { if (!lifetime.IsCancellationRequested) await Task.Delay(100, lifetime.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            }
        })).ToArray();
    }
    private static async Task MonitorAsync(Stream pipe, CancellationTokenSource source)
    {
        try { await pipe.ReadExactlyAsync(new byte[1], source.Token); source.Cancel(); }
        catch (OperationCanceledException) { }
        catch (Exception) { source.Cancel(); }
    }
    private static AgentReply Failure(Exception ex) => new(false, ex switch {
        AgentBusyException => "busy", AgentDeviceUnavailableException => "device_unavailable", UnauthorizedAccessException => "permission_denied", OperationCanceledException => "cancelled",
        System.Text.Json.JsonException or InvalidDataException or ArgumentException => "invalid_input", IOException => "file_or_connection_failed", _ => "execution_failed" }, AgentJson.Empty);
    public void Dispose() { lifetime.Cancel(); _ = Task.WhenAll(workers).ContinueWith(_ => lifetime.Dispose()); }
}
