using SeleVox.Core;

namespace SeleVox.App;

// One OCR's deferred action. This lifetime extends beyond capture to cover pending synthesis.
internal sealed class OcrFollowUp(Settings snapshot, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private bool attempted, disposed, submittedRead;
    internal Settings Snapshot => snapshot;
    internal CancellationToken Cancellation => cancellation.Token;
    internal async Task RunAsync(CapturedText result, Func<bool> current,
        Func<string, Settings, CancellationToken, Task> precheck,
        Action<string, Settings, CancellationToken> read, Action<string> ask)
    {
        if (disposed || attempted || cancellation.IsCancellationRequested || !current()) return;
        attempted = true;
        if (string.IsNullOrWhiteSpace(result.Text)) return;
        switch (snapshot.OcrAction)
        {
            case OcrCompletionAction.AskAi:
                if (!snapshot.AllowsOcrLlm) throw new UserFeedbackException("OCR 自动问 AI 未获当前 LLM 授权，未发送。请在 OCR 区域单独授权。");
                ask(result.Text);
                break;
            case OcrCompletionAction.Read:
                var token = cancellation.Token;
                await precheck(result.Text, snapshot, token);
                if (token.IsCancellationRequested || !current()) return;
                if (snapshot.AutomaticReadDelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(snapshot.AutomaticReadDelaySeconds), timeProvider ?? TimeProvider.System, token);
                if (token.IsCancellationRequested || !current()) return;
                submittedRead = true;
                read(result.Text, snapshot, token);
                break;
        }
    }
    internal void CancelPendingDelayedRead()
    {
        if (!disposed && !submittedRead && snapshot.OcrAction == OcrCompletionAction.Read && snapshot.AutomaticReadDelaySeconds > 0)
            cancellation.Cancel();
    }
    public void Dispose() { if (disposed) return; disposed = true; cancellation.Cancel(); cancellation.Dispose(); }
}
