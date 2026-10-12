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
    [InlineData("{\"Provider\":9}")]
    [InlineData("{\"EdgePitchHz\":101}")]
    [InlineData("{\"EdgeLocaleFilter\":null}")]
    [InlineData("{\"AzureRegion\":\"region.example\"}")]
    [InlineData("{\"OutputDeviceIds\":[null]}")]
    [InlineData("{\"AutoSpeak\":true,\"Applications\":[null]}")]
    [InlineData("{\"AutoSpeak\":true,\"Applications\":[{\"ExecutablePath\":\"editor.exe\"}]}")]
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
    [Fact] public void Pr1ConfigurationDefaultsToAutomaticOff()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathName, "{\"VoiceId\":\"fixture\",\"Rate\":1.5,\"Volume\":0.4,\"StartWithWindows\":true}");
        var loaded = new SettingsStore(PathName).Load();
        Assert.Null(loaded.Warning); Assert.False(loaded.Settings.AutoSpeak);
        Assert.Null(loaded.Settings.Applications); Assert.Equal(1.5, loaded.Settings.Rate);
        Assert.Equal(TtsProvider.Windows, loaded.Settings.Provider); Assert.Null(loaded.Settings.OutputDeviceIds);
        Assert.False(loaded.Settings.AllowAutomaticCloud);
    }
    [Fact] public void Pr2ConfigurationMigratesAndCloudDeviceSettingsRoundTripWithoutKey()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathName, "{\"AutoSpeak\":true,\"Applications\":[{\"ExecutablePath\":\"C:\\\\Apps\\\\fixture.exe\"}]}");
        var store = new SettingsStore(PathName); var old = store.Load(); Assert.Null(old.Warning);
        Assert.True(old.Settings.AutoSpeak); Assert.Equal(TtsProvider.Windows, old.Settings.Provider); Assert.Null(old.Settings.OutputDeviceIds);
        var updated = old.Settings with { Provider = TtsProvider.Azure, AzureRegion = "eastasia", AzureVoice = "fixture", OutputDeviceIds = ["endpoint-a", "endpoint-b"] };
        store.Save(updated); var loaded = store.Load(); Assert.Null(loaded.Warning);
        Assert.Equal(updated.OutputDeviceIds, loaded.Settings.OutputDeviceIds); Assert.Equal(updated.AzureVoice, loaded.Settings.AzureVoice);
        Assert.Equal(TtsProvider.Azure, loaded.Settings.Provider); Assert.Equal("eastasia", loaded.Settings.AzureRegion);
        Assert.DoesNotContain("Key", File.ReadAllText(PathName), StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void AutomaticRulesRoundTripAndRejectRelativePaths()
    {
        var settings = new Settings(AutoSpeak: true, Applications: [new(@"C:\Apps\fixture.exe"), new(@"D:\Other\fixture.exe", false)]);
        var store = new SettingsStore(PathName); store.Save(settings);
        var loaded = store.Load(); Assert.Null(loaded.Warning); Assert.True(loaded.Settings.AutoSpeak);
        Assert.Equal(settings.Applications, loaded.Settings.Applications);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(settings with { Applications = [new("fixture.exe")] }));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    [Fact] public void Pr3AzureConsentNeverBecomesEdgeConsentAndNewSettingsPersist()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathName, "{\"Provider\":1,\"AllowAutomaticCloud\":true,\"AzureRegion\":\"eastasia\"}");
        var store = new SettingsStore(PathName); var old = store.Load();
        Assert.Null(old.Warning); Assert.Equal(TtsProvider.Azure, old.Settings.Provider);
        Assert.False(old.Settings.AllowAutomaticEdge); Assert.Equal(0, old.Settings.EdgePitchHz);
        Assert.False((old.Settings with { Provider = TtsProvider.Edge }).AllowsAutomaticProvider);
        Assert.True((old.Settings with { Provider = TtsProvider.Windows }).AllowsAutomaticProvider);
        var updated = old.Settings with { Provider = TtsProvider.Edge, EdgeVoice = "zh-CN-XiaoxiaoNeural", EdgePitchHz = 20, EdgeLocaleFilter = "en", AllowAutomaticEdge = true };
        store.Save(updated); Assert.Equal(updated, store.Load().Settings);
        Assert.Equal(0, (int)TtsProvider.Windows); Assert.Equal(1, (int)TtsProvider.Azure);
    }
}
