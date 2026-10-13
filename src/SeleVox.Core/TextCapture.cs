namespace SeleVox.Core;

public enum OcrLanguage { ChineseEnglish = 0, English = 1 }
public enum TextSource { Selection, ScreenOcr }
public sealed record CapturedText(string Text, TextSource Source);

// Invoked on the owning UI dispatcher. Cancellation invalidates even an uncooperative operation.
public sealed class TextCaptureOperation : IDisposable
{
    private CancellationTokenSource? current;
    private bool disposed;
    public bool IsActive => current is not null;
    public void Cancel() { var old = current; current = null; old?.Cancel(); }
    public async Task RunAsync(Func<CancellationToken, Task<CapturedText>> operation,
        Action<CapturedText> completed, Action<string> failed, Action? cancelled = null)
    {
        if (disposed) return;
        Cancel();
        using var request = new CancellationTokenSource(); current = request;
        try
        {
            var result = await operation(request.Token);
            if (ReferenceEquals(current, request) && !request.IsCancellationRequested) completed(result);
        }
        catch (OperationCanceledException) { if (ReferenceEquals(current, request)) cancelled?.Invoke(); }
        catch (Exception ex)
        {
            if (ReferenceEquals(current, request) && !request.IsCancellationRequested)
                failed(ex is UserFeedbackException ? ex.Message : $"屏幕 OCR 失败（{ex.GetType().Name}），请重试。");
        }
        finally { if (ReferenceEquals(current, request)) current = null; }
    }
    public void Dispose() { disposed = true; Cancel(); }
}
