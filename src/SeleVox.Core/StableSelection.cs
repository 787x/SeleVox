namespace SeleVox.Core;

public sealed record SelectionSnapshot(string Text, string Focus, string? UnavailableReason = null);

public static class StableSelection
{
    // Empty/caret selections can be transient while the target publishes a mouse selection.
    // Only two consecutive nonempty samples with the same focus and text are submitted.
    public static async Task<SelectionSnapshot> CaptureAsync(
        Func<string?, CancellationToken, Task<SelectionSnapshot>> capture, CancellationToken cancellation,
        int initialDelayMilliseconds = 180, int sampleDelayMilliseconds = 100, int maximumSamples = 6)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1500));
        string? focus = null;
        SelectionSnapshot? previous = null;
        var sawText = false;
        string? unavailableReason = null;
        try
        {
            await Task.Delay(initialDelayMilliseconds, budget.Token);
            for (var sample = 0; sample < maximumSamples; sample++)
            {
                var current = await capture(focus, budget.Token).WaitAsync(budget.Token);
                unavailableReason = current.UnavailableReason;
                budget.Token.ThrowIfCancellationRequested();
                focus ??= current.Focus;
                if (focus != current.Focus) throw new UserFeedbackException("焦点已改变，已忽略自动选区。");
                if (!string.IsNullOrWhiteSpace(current.Text))
                {
                    sawText = true;
                    if (previous is not null && previous.Text == current.Text) return current;
                    previous = current;
                }
                else previous = null;
                if (sample + 1 < maximumSamples) await Task.Delay(sampleDelayMilliseconds, budget.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new UserFeedbackException("自动选区未能及时稳定，请重新选择或使用手动热键。"); }
        throw new UserFeedbackException(unavailableReason ?? (sawText ? "选区仍在变化，已忽略此次自动朗读。" : "没有选中文字。"));
    }
}
