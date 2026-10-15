using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class StableSelectionTests
{
    private static Task<SelectionSnapshot> Capture(params SelectionSnapshot[] samples)
    {
        var next = 0;
        return StableSelection.CaptureAsync((focus, token) =>
        {
            token.ThrowIfCancellationRequested();
            var result = samples[next++];
            if (next > 1) Assert.Equal("focus", focus);
            return Task.FromResult(result);
        }, CancellationToken.None, 0, 0, samples.Length);
    }
    [Fact] public async Task TransientEmptyAndLateGeometryCanRecoverWithoutAnotherGesture()
    {
        var result = await Capture(new("", "focus"), new("", "focus", "geometry pending"),
            new("fixture", "focus"), new("fixture", "focus"));
        Assert.Equal("fixture", result.Text);
    }
    [Fact] public async Task ChangedTextSettlesOnLatestAndEmptyBreaksConsecutiveSamples()
    {
        var result = await Capture(new("old fixture", "focus"), new("latest fixture", "focus"),
            new("", "focus"), new("latest fixture", "focus"), new("latest fixture", "focus"));
        Assert.Equal("latest fixture", result.Text);
    }
    [Fact] public async Task FocusChangeNeverRetriesIntoAnotherControl()
    {
        var calls = 0;
        await Assert.ThrowsAsync<UserFeedbackException>(() => StableSelection.CaptureAsync((_, _) =>
            Task.FromResult(new SelectionSnapshot("fixture", ++calls == 1 ? "first" : "other")), CancellationToken.None, 0, 0));
        Assert.Equal(2, calls);
    }
    [Fact] public async Task PersistentEmptyIsBoundedAndDoesNotSubmitToPlayback()
    {
        using var automatic = new AutomaticSelection();
        var calls = 0; var submitted = false; string? message = null;
        await automatic.RequestAsync("app", async token => (await StableSelection.CaptureAsync((_, _) =>
        {
            ++calls; return Task.FromResult(new SelectionSnapshot("", "focus"));
        }, token, 0, 0)).Text, () => true, _ => submitted = true, error => message = error);
        Assert.Equal(6, calls); Assert.False(submitted); Assert.Equal("没有选中文字。", message);
    }
    [Fact] public async Task PersistentGeometryMismatchReportsItsActualReason()
    {
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => Capture(
            new("", "focus", "geometry mismatch"), new("", "focus", "geometry mismatch")));
        Assert.Equal("geometry mismatch", error.Message);
    }
    [Fact] public async Task SecurityOrProviderFailureIsNotRetried()
    {
        var calls = 0;
        await Assert.ThrowsAsync<UserFeedbackException>(() => StableSelection.CaptureAsync((_, _) =>
        {
            ++calls; throw new UserFeedbackException("password rejected");
        }, CancellationToken.None, 0, 0));
        Assert.Equal(1, calls);
    }
    [Fact] public async Task StopWhileRetryingPreventsLateSelectionFromBeingSubmitted()
    {
        using var automatic = new AutomaticSelection();
        var pending = new TaskCompletionSource<SelectionSnapshot>(); var calls = 0; var submitted = false;
        var request = automatic.RequestAsync("app", async token => (await StableSelection.CaptureAsync((_, _) =>
        {
            ++calls;
            return calls == 1 ? Task.FromResult(new SelectionSnapshot("", "focus")) : pending.Task;
        }, token, 0, 0)).Text, () => true, _ => submitted = true);
        Assert.Equal(2, calls); automatic.Invalidate();
        pending.SetResult(new("fixture", "focus")); await request;
        Assert.False(submitted); Assert.Equal(2, calls);
    }
    [Fact] public async Task NonCooperativeProviderHasTotalTimeBudgetAndNoRetryAccumulation()
    {
        var pending = new TaskCompletionSource<SelectionSnapshot>(); var calls = 0;
        var error = await Assert.ThrowsAsync<UserFeedbackException>(() => StableSelection.CaptureAsync((_, _) =>
        { ++calls; return pending.Task; }, CancellationToken.None, 0, 0));
        pending.SetResult(new("fixture", "focus"));
        Assert.Equal(1, calls); Assert.Contains("及时稳定", error.Message);
    }
}
