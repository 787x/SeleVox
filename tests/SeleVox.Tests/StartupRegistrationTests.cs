using Microsoft.Win32;
using SeleVox.App;
namespace SeleVox.Tests;
[Trait("Category", "Offline")]
public sealed class StartupRegistrationTests
{
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void FailedSaveRestoresExactRegistration(bool wasEnabled, bool enabled)
    {
        var subkey = @"Software\SeleVox-StartupTest-" + Guid.NewGuid();
        var directory = Path.Combine(Path.GetTempPath(), "SeleVox-startup-" + Guid.NewGuid());
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            var store = new SeleVox.Core.SettingsStore(Path.Combine(directory, "settings.json"));
            var saved = new SeleVox.Core.Settings(StartWithWindows: wasEnabled);
            store.Save(saved);
            if (wasEnabled) key.SetValue("SeleVox", StartupRegistration.Command(@"C:\old portable\SeleVox.App.exe"), RegistryValueKind.ExpandString);
            var previous = key.GetValue("SeleVox", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            // Lock the destination so the real atomic file replacement fails.
            using (var locked = File.Open(Path.Combine(directory, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<UnauthorizedAccessException>(() => StartupRegistration.ApplyAndSave(key, enabled, @"C:\new setup\SeleVox.App.exe",
                    () => store.Save(saved with { StartWithWindows = enabled })));
            Assert.Equal(previous, key.GetValue("SeleVox", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            if (wasEnabled) Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("SeleVox"));
            Assert.Equal(saved, store.Load().Settings);
            StartupRegistration.ApplyAndSave(key, enabled, @"C:\new setup\SeleVox.App.exe", () => store.Save(saved with { StartWithWindows = enabled }));
            Assert.Equal(enabled, store.Load().Settings.StartWithWindows);
            Assert.Equal(enabled ? StartupRegistration.Command(@"C:\new setup\SeleVox.App.exe") : null, key.GetValue("SeleVox"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void RegistrationFailuresNeverPersistOrOverwriteConflicts()
    {
        var subkey = @"Software\SeleVox-StartupTest-" + Guid.NewGuid();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            var saves = 0;
            using (var readOnly = Registry.CurrentUser.OpenSubKey(subkey, false)!)
                Assert.Throws<UnauthorizedAccessException>(() => StartupRegistration.ApplyAndSave(readOnly, true, @"C:\test\SeleVox.App.exe", () => ++saves));
            key.SetValue("SeleVox", "other.exe");
            Assert.Throws<StartupRegistrationException>(() => StartupRegistration.ApplyAndSave(key, true, @"C:\test\SeleVox.App.exe", () => ++saves));
            Assert.Equal(0, saves); Assert.Equal("other.exe", key.GetValue("SeleVox"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
    }
    [Fact]
    public void FailedDisableSaveLeavesForeignBinaryValueUntouched()
    {
        var subkey = @"Software\SeleVox-StartupTest-" + Guid.NewGuid();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            key.SetValue("SeleVox", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            Assert.Throws<IOException>(() => StartupRegistration.ApplyAndSave(key, false, null, () => throw new IOException()));
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(key.GetValue("SeleVox")));
            Assert.Equal(RegistryValueKind.Binary, key.GetValueKind("SeleVox"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
    }
    [Fact]
    public void RollbackFailureIsExplicitWithoutExposingExceptionDetails()
    {
        var subkey = @"Software\SeleVox-StartupTest-" + Guid.NewGuid();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            var error = Assert.Throws<StartupRegistrationException>(() => StartupRegistration.ApplyAndSave(key, true, @"C:\test\SeleVox.App.exe", () =>
            {
                key.SetValue("SeleVox", "foreign.exe");
                throw new IOException("private details");
            }));
            Assert.Contains("恢复失败", error.Message); Assert.DoesNotContain("private details", error.Message);
            Assert.Equal("foreign.exe", key.GetValue("SeleVox"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
    }
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
