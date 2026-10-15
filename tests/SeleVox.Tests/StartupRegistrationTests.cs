using Microsoft.Win32;
using SeleVox.App;
namespace SeleVox.Tests;
[Trait("Category", "Offline")]
public sealed class StartupRegistrationTests
{
    [Theory]
    [InlineData(@"C:\Program Files\中文\SeleVox.App.exe")]
    [InlineData(@"D:\Portable\SeleVox.App.exe")]
    public void RunRoundTripMigrationAndUnrelatedValues(string path)
    {
        var subkey = @"Software\SeleVox-StartupTest-" + Guid.NewGuid();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            key.SetValue("OtherApp", "untouched");
            StartupRegistration.Apply(key, true, @"C:\old portable\SeleVox.App.exe");
            StartupRegistration.Apply(key, true, path);
            var command = StartupRegistration.Command(path);
            Assert.Equal($"\"{path}\" --background", key.GetValue("SeleVox"));
            Assert.True(StartupRegistration.IsSeleVoxCommand(command));
            StartupRegistration.Apply(key, true, path);
            Assert.Equal(command, key.GetValue("SeleVox"));
            // A matching value must succeed even when the handle cannot write.
            using (var readOnly = Registry.CurrentUser.OpenSubKey(subkey, writable: false)!)
                StartupRegistration.Apply(readOnly, true, path);
            StartupRegistration.Apply(key, false, path);
            Assert.Null(key.GetValue("SeleVox"));
            using (var readOnly = Registry.CurrentUser.OpenSubKey(subkey, writable: false)!)
                StartupRegistration.Apply(readOnly, false, path);
            key.SetValue("SeleVox", "other.exe --do-not-touch");
            Assert.Throws<StartupRegistrationException>(() => StartupRegistration.Apply(key, true, path));
            StartupRegistration.Apply(key, false, path);
            Assert.Equal("other.exe --do-not-touch", key.GetValue("SeleVox"));
            Assert.Equal("untouched", key.GetValue("OtherApp"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
    }
    [Theory]
    [InlineData(null)] [InlineData("SeleVox.App.exe")] [InlineData(@"C:\bad?\SeleVox.App.exe")] [InlineData(@"C:\wrong.exe")]
    [InlineData("C:\\bad\"\\SeleVox.App.exe")]
    public void InvalidPathsFailExplicitly(string? path) => Assert.Throws<StartupRegistrationException>(() => StartupRegistration.Command(path));
    [Fact] public void LongRunCommandRejected() => Assert.Throws<StartupRegistrationException>(() => StartupRegistration.Command(@"C:\" + new string('x', 250) + @"\SeleVox.App.exe"));
    [Theory] [InlineData("--background")] [InlineData("--agent-background")]
    public void BothBackgroundModesSuppressShow(string argument)
    {
        Assert.True(SingleInstance.IsBackground([argument]));
        Assert.False(SingleInstance.IsBackground([]));
        var scope = "SeleVox-test-" + Guid.NewGuid();
        using var shown = new ManualResetEventSlim();
        using var first = new SingleInstance(scope); first.Listen(() => shown.Set());
        using var second = new SingleInstance(scope, showExisting: !SingleInstance.IsBackground([argument]));
        Assert.False(second.IsFirst);
        Assert.False(shown.Wait(TimeSpan.FromMilliseconds(150)));
    }
    [Fact] public void ManualSecondInstanceSignalsShow()
    {
        var scope = "SeleVox-test-" + Guid.NewGuid();
        using var shown = new ManualResetEventSlim();
        using var first = new SingleInstance(scope); first.Listen(() => shown.Set());
        using var second = new SingleInstance(scope, showExisting: !SingleInstance.IsBackground([]));
        Assert.False(second.IsFirst);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(3)));
    }
}
