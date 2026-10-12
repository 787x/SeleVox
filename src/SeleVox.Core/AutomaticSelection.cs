using System.Security.Cryptography;
using System.Text;

namespace SeleVox.Core;

// Capture is separate from submission: failed, stale and duplicate captures never touch audio.
public sealed class AutomaticSelection : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? pending;
    private long generation;
    private string? lastFingerprint;
    private bool disposed;

    public async Task RequestAsync(string identity, Func<CancellationToken, Task<string>> capture,
        Func<bool> stillAllowed, Action<string> submit, Action<string>? feedback = null)
    {
        CancellationTokenSource request;
        long id;
        lock (sync)
        {
            if (disposed) return;
            pending?.Cancel();
            pending = request = new();
            id = ++generation;
        }
        try
        {
            var text = await capture(request.Token);
            if (string.IsNullOrWhiteSpace(text)) return;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\0" + text)));
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested || !stillAllowed() ||
                    fingerprint == lastFingerprint) return;
                submit(text);
                lastFingerprint = fingerprint;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
                if (!disposed && id == generation && !request.IsCancellationRequested)
                    feedback?.Invoke(ex is UserFeedbackException ? ex.Message : "自动选区读取失败，请尝试手动快捷键。");
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(pending, request)) pending = null; }
            request.Dispose();
        }
    }

    public void Invalidate(bool resetDuplicate = false)
    {
        lock (sync)
        {
            ++generation; pending?.Cancel(); pending = null;
            if (resetDuplicate) lastFingerprint = null;
        }
    }
    public void Dispose() { lock (sync) { disposed = true; Invalidate(true); } }
}
