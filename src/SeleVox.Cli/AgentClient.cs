using System.Diagnostics;
using System.IO.Pipes;
using SeleVox.Core;
namespace SeleVox.Cli;
public sealed class AgentClient : IDisposable
{
    private readonly object sync = new();
    private readonly HashSet<NamedPipeClientStream> leases = new();
    private readonly string? pipeName;
    private bool disposed;
    public AgentClient(string? pipeName = null) { this.pipeName = pipeName; }
    public async Task<AgentReply> CallAsync(string command, object arguments, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var pipe = await ConnectAsync(timeout.Token); bool retained = false;
        try
        {
            var fields = AgentJson.Element(arguments);
            await AgentWire.WriteAsync(pipe, new AgentRequest(1, command, fields), timeout.Token);
            var reply = await AgentWire.ReadAsync<AgentReply>(pipe, timeout.Token);
            if (reply.Success && command is "speak" or "synthesize" && fields.TryGetProperty("mcp", out var mcp) && mcp.GetBoolean())
            {
                lock (sync) { if (disposed) throw new OperationCanceledException(); leases.Add(pipe); retained = true; }
                _ = DrainLeaseAsync(pipe);
            }
            return reply;
        }
        finally { if (!retained) pipe.Dispose(); }
    }
    private async Task DrainLeaseAsync(NamedPipeClientStream pipe)
    {
        try { await pipe.ReadExactlyAsync(new byte[1]); } catch (Exception) { }
        finally { lock (sync) leases.Remove(pipe); pipe.Dispose(); }
    }
    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        NamedPipeClientStream NewPipe() => new(".", pipeName ?? AgentWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var pipe = NewPipe();
        try { await pipe.ConnectAsync(500, token); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); }
        if (pipeName is not null) throw new TimeoutException();
        var executable = Path.Combine(AppContext.BaseDirectory, "SeleVox.App.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("desktop_not_found");
        // ShellExecute starts this fixed local WinExe without inheriting the caller's pipe handles.
        // Redirecting just standard streams still inherits other handles on Windows CreateProcess.
        using var process = Process.Start(new ProcessStartInfo(executable, "--agent-background") {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        pipe = NewPipe();
        try { await pipe.ConnectAsync(15000, token); return pipe; }
        catch { pipe.Dispose(); throw; }
    }
    public void Dispose() { lock (sync) { disposed = true; foreach (var pipe in leases) pipe.Dispose(); leases.Clear(); } }
}
