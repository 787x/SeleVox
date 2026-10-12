using SeleVox.App;
namespace SeleVox.Tests;
public sealed class WindowsLifetimeTests
{
    [Fact] public void SingleInstanceSignalsOriginalAndReleasesMutex()
    {
        RunSta(() =>
        {
            var scope = "SeleVox-test-" + Guid.NewGuid();
            using var shown = new ManualResetEventSlim();
            using (var first = new SingleInstance(scope))
            {
                Assert.True(first.IsFirst); first.Listen(() => shown.Set());
                using var second = new SingleInstance(scope);
                Assert.False(second.IsFirst); Assert.True(shown.Wait(TimeSpan.FromSeconds(3)));
            }
            using var restarted = new SingleInstance(scope); Assert.True(restarted.IsFirst);
        });
    }
    [Fact] public void GlobalHotkeyConflictsAreReportedAndDisposeUnregisters()
    {
        RunSta(() =>
        {
            using (var first = new Hotkeys())
            {
                Assert.Empty(first.Conflicts);
                using var second = new Hotkeys(); Assert.Equal(3, second.Conflicts.Count);
            }
            using var restarted = new Hotkeys(); Assert.Empty(restarted.Conflicts);
        });
    }
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
