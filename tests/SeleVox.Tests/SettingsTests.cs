using SeleVox.Core;
namespace SeleVox.Tests;
public sealed class SettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SeleVox-tests-" + Guid.NewGuid());
    private string PathName => Path.Combine(directory, "settings.json");
    [Fact] public void MissingFileUsesSafeDefaultsAndWarning()
    {
        var result = new SettingsStore(PathName).Load();
        Assert.Equal(new Settings(), result.Settings); Assert.NotNull(result.Warning);
        Assert.False(result.Settings.StartWithWindows);
    }
    [Theory]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("{\"Rate\":10}")]
    [InlineData("{\"Volume\":-1}")]
    public void InvalidFileUsesDefaults(string json)
    {
        Directory.CreateDirectory(directory); File.WriteAllText(PathName, json);
        var result = new SettingsStore(PathName).Load();
        Assert.Equal(new Settings(), result.Settings); Assert.NotNull(result.Warning);
    }
    [Fact] public void SavesAndOverwritesAllPreferences()
    {
        var store = new SettingsStore(PathName);
        store.Save(new("voice-a", 1.5, 0.4f, true));
        var updated = new Settings("voice-b", 0.75, 0.6f, false);
        store.Save(updated);
        Assert.Equal(updated, store.Load().Settings); Assert.Null(store.Load().Warning);
        Assert.False(File.Exists(PathName + ".tmp"));
    }
    [Fact] public void InvalidValuesCannotBeSaved()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingsStore(PathName).Save(new(Rate: double.NaN)));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
