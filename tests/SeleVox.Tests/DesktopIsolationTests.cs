using System.Diagnostics;
using System.Text;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class DesktopIsolationTests
{
    [Fact] public async Task SeparateProcessesCannotOwnDesktopLeaseSimultaneously()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); Exception? failure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var scope = DesktopTestScope.Acquire(); entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Lease test was not released.");
            }
            catch (Exception ex) { failure = ex; entered.TrySetException(ex); }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("blocked", await Probe());
        }
        finally { release.Set(); Assert.True(owner.Join(TimeSpan.FromSeconds(5))); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal("available", await Probe());
    }
    private static async Task<string> Probe()
    {
        const string script = @"$m=[Threading.Mutex]::new($false,'Local\SeleVox.Tests.Desktop'); $owns=$false; try { $owns=$m.WaitOne(0); if($owns){'available'}else{'blocked'} } finally {if($owns){$m.ReleaseMutex()};$m.Dispose()}";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, process.ExitCode);
            return (await process.StandardOutput.ReadToEndAsync()).Trim();
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
