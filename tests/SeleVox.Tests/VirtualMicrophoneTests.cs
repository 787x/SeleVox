using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
[Trait("Category", "Offline")]
public sealed class VirtualMicrophoneTests(ITestOutputHelper log)
{
    private const string Driver = @"ROOT\VBAUDIOVACWDM\0000";
    private static VirtualEndpoint[] Pair(DeviceState state = DeviceState.Active) =>
        [new("render", "renamed playback", DataFlow.Render, state, Driver), new("capture", "renamed mic", DataFlow.Capture, state, Driver)];
    private static readonly VirtualMicrophoneRoute Route = new("render", "capture", Driver, true);
    private static Settings Enabled(string[]? ids = null) => new(OutputDeviceIds: ids) { VirtualMicrophoneEnabled = true, VirtualMicrophone = Route };
    [Fact] public void PairUsesDriverIdentityAndBothEndpointIdsNotNames()
    {
        Assert.True(VirtualMicrophones.IsReady(Route, Pair()));
        Assert.False(VirtualMicrophones.IsReady(Route, Pair(DeviceState.Disabled)));
        Assert.False(VirtualMicrophones.IsReady(Route, [Pair()[0]]));
        Assert.False(VirtualMicrophones.IsReady(Route with { RecordingVerified = false }, Pair()));
        Assert.False(VirtualMicrophones.IsReady(Route, [Pair()[0], Pair()[1] with { DriverInstanceId = @"ROOT\MEDIA\0000" }]));
        Assert.False(VirtualMicrophones.IsReady(Route, [Pair()[0] with { Id = "replacement" }, Pair()[1]]));
        Assert.NotNull(VirtualMicrophones.Detect(Pair()));
        Assert.Null(VirtualMicrophones.Detect([.. Pair(), Pair()[0] with { Id = "other pin" }]));
        Assert.False(VirtualMicrophones.IsSupportedDriver(@"ROOT\MEDIA\0000"));
    }
    [Fact] public void PersistenceAndOldSettingsRetainConservativeDefaults()
    {
        var old = JsonSerializer.Deserialize<Settings>("{\"OutputDeviceIds\":[\"speaker\"]}")!;
        Assert.True(old.IsValid); Assert.False(old.VirtualMicrophoneEnabled); Assert.Null(old.VirtualMicrophone);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json")); store.Save(Enabled(["speaker"]));
            var loaded = store.Load(); Assert.Null(loaded.Warning); Assert.Equal(Route, loaded.Settings.VirtualMicrophone);
            Assert.Equal(new[] { "speaker" }, loaded.Settings.OutputDeviceIds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact] public void ResolvePreservesPhysicalOutputsAndDeduplicatesAndNeverSwitchesPair()
    {
        Assert.Equal(new[] { Settings.DefaultOutput, "render" }, VirtualMicrophones.Resolve(Enabled(), Pair()).OutputDeviceIds);
        Assert.Equal(new[] { "speaker", "render" }, VirtualMicrophones.Resolve(Enabled(["speaker", "render", "speaker"]), Pair()).OutputDeviceIds);
        var missing = VirtualMicrophones.Resolve(Enabled(["render"]), []);
        Assert.Equal(new[] { "render" }, missing.UnavailableOutputIds); Assert.False(VirtualMicrophones.CanFallback(missing));
        Assert.True(VirtualMicrophones.CanFallback(VirtualMicrophones.Resolve(Enabled(["speaker"]), [])));
        var disabled = Enabled() with { VirtualMicrophoneEnabled = false };
        Assert.Equal(new[] { Settings.DefaultOutput }, VirtualMicrophones.Resolve(disabled, []).OutputDeviceIds);
        var duplicate = Enabled(["speaker", "render"]) with { VirtualMicrophoneEnabled = false };
        Assert.Equal(new[] { "speaker" }, VirtualMicrophones.Resolve(duplicate, []).OutputDeviceIds);
        Assert.Throws<UserFeedbackException>(() => VirtualMicrophones.Resolve(duplicate with { OutputDeviceIds = ["render"] }, []));
        Assert.Equal(new[] { "render" }, VirtualMicrophones.Resolve(duplicate with { OutputDeviceIds = ["render"], ExplicitOutputDevices = true }, []).OutputDeviceIds);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void MissingVirtualOutputDoesNotOpenOrFallbackAndPhysicalSurvives(bool streaming)
    {
        var opened = new List<string>(); var settings = VirtualMicrophones.Resolve(Enabled(["speaker"]), []);
        IAudioPlayback Open(string id, IDisposable stream) { opened.Add(id); return new Player(stream); }
        using var buffer = new StreamingPcmBuffer();
        using var group = streaming ? AudioOutputs.CreateStreamingPlayback(buffer, settings, (id, reader, _) => Open(id, reader))
            : AudioOutputs.CreatePlayback(MultiOutputTests.Fixture(), settings, (id, stream, _) => Open(id, stream));
        Assert.Equal(new[] { "speaker" }, opened); Assert.Contains("虚拟麦克风", group.Warning);
        settings = VirtualMicrophones.Resolve(Enabled(["render"]), []); opened.Clear();
        Assert.Throws<UserFeedbackException>(() => streaming ? AudioOutputs.CreateStreamingPlayback(buffer, settings, (id, reader, _) => Open(id, reader))
            : AudioOutputs.CreatePlayback(MultiOutputTests.Fixture(), settings, (id, stream, _) => Open(id, stream)));
        Assert.Empty(opened);
    }
    [Theory] [InlineData("{\"devices\":[\"speaker\"]}")] [InlineData("{\"devices\":[]}")] [InlineData("{\"devices\":null}")]
    public void AgentExplicitDeviceFieldsOverrideVirtualRouteIncludingEmptyAndNull(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = AgentParameters.Resolve(Enabled(), new(), null, document.RootElement).Settings;
        Assert.False(result.VirtualMicrophoneEnabled); Assert.True(result.StrictOutputDevices);
        var inherited = AgentParameters.Resolve(Enabled(), new(), null, AgentJson.Empty).Settings;
        Assert.True(inherited.VirtualMicrophoneEnabled); Assert.False(inherited.StrictOutputDevices);
        var preset = new AgentConfiguration(); preset.Presets["single"] = document.RootElement.Clone();
        Assert.False(AgentParameters.Resolve(Enabled(), preset, "single", AgentJson.Empty).Settings.VirtualMicrophoneEnabled);
    }
    [Fact] public void StreamingStrictFailureDisposesSurvivorWithoutDefaultFallback()
    {
        using var buffer = new StreamingPcmBuffer(); var opened = new List<string>(); Player? partial = null;
        Assert.Throws<UserFeedbackException>(() => AudioOutputs.CreateStreamingPlayback(buffer, new(OutputDeviceIds: ["speaker", "missing"]) { StrictOutputDevices = true },
            (id, reader, _) => { opened.Add(id); if (id == "missing") throw new IOException(); return partial = new Player(reader); }));
        Assert.Equal(new[] { "speaker", "missing" }, opened); Assert.True(partial!.Disposed);
    }
    [Fact] public void ExplicitConfiguredVirtualTargetStillRequiresItsCaptureAndNeverFallsBack()
    {
        var settings = Enabled(["render"]) with { VirtualMicrophoneEnabled = false, ExplicitOutputDevices = true, StrictOutputDevices = true };
        var absent = VirtualMicrophones.Resolve(settings, [Pair()[0]]);
        Assert.Equal(new[] { "render" }, absent.UnavailableOutputIds);
        Assert.Throws<UserFeedbackException>(() => AudioOutputs.CreatePlayback(MultiOutputTests.Fixture(), absent, (_, _, _) => throw new InvalidOperationException("must not open")));
        Assert.Empty(VirtualMicrophones.Resolve(settings, Pair()).UnavailableOutputIds);
    }
    [Fact] public void RecordingCorrelationRejectsSilenceAndUnrelatedWaveform()
    {
        using var tone = VirtualMicrophoneTest.Tone(); using var reader = new NAudio.Wave.WaveFileReader(tone);
        var sample = reader.ToSampleProvider(); var floats = new float[72000]; Assert.Equal(floats.Length, sample.Read(floats));
        Assert.InRange(VirtualMicrophoneTest.Correlation(floats, 24000), .99, 1.01);
        Assert.Equal(0, VirtualMicrophoneTest.Correlation(new float[72000], 24000));
        Assert.True(VirtualMicrophoneTest.Correlation(Enumerable.Range(0, 72000).Select(i => (float)(.2 * Math.Sin(i * 2 * Math.PI * 440 / 24000))).ToArray(), 24000) < .1);
    }
    [Fact] public void LateOldPlaybackStatusCannotOverwriteNewRouteActivity()
    {
        var old = new StatusPlayer(); using var first = VirtualMicrophones.Track(old, Route.RenderId, Enabled());
        first.Play(); var late = old.Callback!; first.Stop(); first.Dispose();
        var next = new StatusPlayer(); using var second = VirtualMicrophones.Track(next, Route.RenderId, Enabled());
        second.Play(); late(null); Assert.Equal("正在路由", VirtualMicrophones.Activity(Route.RenderId));
        second.Pause(); Assert.Equal("路由已暂停", VirtualMicrophones.Activity(Route.RenderId));
        second.Stop(); Assert.Equal("", VirtualMicrophones.Activity(Route.RenderId));
    }
    [VirtualMicFact] public async Task RealCaptureReceivesFixedPcmFromSelectedVbCableRender()
    {
        var devices = VirtualMicrophones.Scan();
        Assert.NotEmpty(devices);
        foreach (var d in devices) log.WriteLine($"{d.Flow}: {d.Label}; {d.State}");
        var capture = devices.First(d => d.Flow == DataFlow.Capture && d.State == DeviceState.Active);
        var renders = devices.Where(d => d.Flow == DataFlow.Render && d.State == DeviceState.Active && d.DriverInstanceId == capture.DriverInstanceId).ToArray();
        Assert.NotEmpty(renders);
        foreach (var render in renders)
        {
            var route = new VirtualMicrophoneRoute(render.Id, capture.Id, render.DriverInstanceId);
            var correlation = await VirtualMicrophoneTest.RunAsync(route, default);
            log.WriteLine($"Real Capture recording from {render.Label}: normalized PCM correlation={correlation:F4}; 3 s fixed PCM, no loopback capture.");
            Assert.True(correlation >= .6);
        }
    }
    private sealed class Player(IDisposable resource) : IAudioPlayback
    {
        public bool Disposed;
        public event Action<Exception?>? Finished { add { } remove { } }
        public void Play() { } public void Pause() { } public void Stop() { }
        public void Dispose() { resource.Dispose(); Disposed = true; }
    }
    private sealed class StatusPlayer : IAudioPlayback
    {
        public event Action<Exception?>? Finished;
        public Action<Exception?>? Callback => Finished;
        public void Play() { } public void Pause() { } public void Stop() { } public void Dispose() { }
    }
}
public sealed class VirtualMicFactAttribute : FactAttribute
{
    public VirtualMicFactAttribute() { if (Environment.GetEnvironmentVariable("SELEVOX_VIRTUAL_MIC_TESTS") != "1") Skip = "Opt-in real VB-CABLE Capture recording; emits fixed test PCM."; }
}
