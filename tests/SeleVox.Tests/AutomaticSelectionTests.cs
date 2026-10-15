using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class AutomaticSelectionTests
{
    [Fact] public async Task InvalidDuplicateAndDisallowedSelectionsNeverSubmit()
    {
        using var automatic = new AutomaticSelection();
        var submitted = new List<string>();
        Task Request(string text, bool allowed = true) => automatic.RequestAsync("app|window", _ => Task.FromResult(text),
            () => allowed, submitted.Add);
        await Request(" "); await Request("fixture", false);
        await automatic.RequestAsync("app", _ => throw new UserFeedbackException("no selection"), () => true, submitted.Add);
        Assert.Empty(submitted);
        await Request("fixture"); await Request("fixture");
        Assert.Single(submitted);
        await Request("different fixture"); Assert.Equal(2, submitted.Count);
        await automatic.RequestAsync("other app", _ => Task.FromResult("fixture"), () => true, submitted.Add);
        Assert.Equal(3, submitted.Count);
    }
    [Fact] public async Task LatestCaptureWinsEvenIfOldProviderIgnoresCancellation()
    {
        using var automatic = new AutomaticSelection();
        var old = new TaskCompletionSource<string>(); var latest = new TaskCompletionSource<string>();
        var submitted = new List<string>();
        var first = automatic.RequestAsync("app", _ => old.Task, () => true, submitted.Add);
        var second = automatic.RequestAsync("app", _ => latest.Task, () => true, submitted.Add);
        latest.SetResult("latest fixture"); await second;
        old.SetResult("old fixture"); await first;
        Assert.Equal(["latest fixture"], submitted);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopDisableAndDisposeRejectLateCapture(bool dispose)
    {
        using var automatic = new AutomaticSelection();
        var pending = new TaskCompletionSource<string>(); var submitted = false;
        var read = automatic.RequestAsync("app", _ => pending.Task, () => true, _ => submitted = true);
        if (dispose) automatic.Dispose(); else automatic.Invalidate();
        pending.SetResult("fixture"); await read;
        Assert.False(submitted);
    }
    [Fact] public async Task FocusOrRuleChangeBeforeCommitRejectsResult()
    {
        using var automatic = new AutomaticSelection();
        var pending = new TaskCompletionSource<string>(); var allowed = true; var submitted = false;
        var read = automatic.RequestAsync("app", _ => pending.Task, () => allowed, _ => submitted = true);
        allowed = false; pending.SetResult("fixture"); await read;
        Assert.False(submitted);
    }
    [Fact] public async Task NewGestureCancelsDebounceBeforeCapture()
    {
        using var automatic = new AutomaticSelection(); var captures = 0;
        var read = automatic.RequestAsync("app", async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token); ++captures; return "fixture";
        }, () => true, _ => { });
        automatic.Invalidate(); await read;
        Assert.Equal(0, captures);
    }
    [Fact] public void OnlyDragAndDoubleClickQualifyAndWindowSwitchResets()
    {
        var gesture = new MouseSelectionGesture(500, 8, 8);
        gesture.Down(1, 10, 10, 10); Assert.False(gesture.Up(1, 11, 10, 20));
        gesture.Down(1, 11, 10, 100); Assert.True(gesture.Up(1, 11, 10, 110));
        gesture.Down(1, 10, 10, 200); Assert.False(gesture.Up(1, 10, 10, 210));
        gesture.Down(1, 40, 40, 250); Assert.False(gesture.Up(1, 40, 40, 260));
        gesture.Down(1, 10, 10, 800); Assert.True(gesture.Up(1, 50, 10, 850));
        gesture.Down(1, 10, 10, 900); Assert.False(gesture.Up(2, 50, 10, 950));
        gesture.Down(1, 10, 10, 1000); gesture.Reset(); Assert.False(gesture.Up(1, 50, 10, 1050));
        gesture.Down(1, 10, 10, 1100); Assert.False(gesture.Up(1, 10, 10, 3000));
        gesture.Down(1, 10, 10, 3100); Assert.False(gesture.Up(1, 10, 10, 3150));
    }
    [Fact] public void MatchingUsesEntirePathAndRequiresExplicitEnable()
    {
        var settings = new Settings(AutoSpeak: true, Applications: [new(@"C:\Apps\editor.exe"), new(@"D:\Apps\disabled.exe", false)]);
        Assert.True(settings.AllowsAutomatic(@"c:\apps\EDITOR.exe"));
        Assert.False(settings.AllowsAutomatic(@"D:\Apps\editor.exe"));
        Assert.False(settings.AllowsAutomatic(@"D:\Apps\disabled.exe"));
        Assert.False(settings.AllowsAutomatic("editor.exe")); Assert.False(settings.AllowsAutomatic(null));
        Assert.False((settings with { AutoSpeak = false }).AllowsAutomatic(@"C:\Apps\editor.exe"));
        Assert.False((settings with { Applications = [] }).AllowsAutomatic(@"C:\Apps\editor.exe"));
    }
}
