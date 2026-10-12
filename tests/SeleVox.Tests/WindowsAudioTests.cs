using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using NAudio.CoreAudioApi;
using Xunit.Abstractions;

namespace SeleVox.Tests;

// Opt-in hardware tests: run with --filter Category=WindowsAudio on a desktop with
// an installed WinRT voice and a default render device. They emit fixture speech.
public sealed class WindowsAudioTests(ITestOutputHelper log)
{
    [VoicemeeterAudioFact, Trait("Category", "WindowsAudio")]
    public async Task VoicemeeterInputReceivesNonSilentSpeechInEndpointLoopback()
    {
        var selected = AudioOutputs.GetDevices().Single(d => d.Label == "Voicemeeter Input (VB-Audio Voicemeeter VAIO)");
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(selected.Id);
        using var recorder = new WasapiRecorderBuilder().WithDevice(endpoint).WithLoopbackCapture()
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
        double peak = 0;
        var captureSync = new object();
        recorder.DataAvailable += (buffer, _, _, _) =>
        {
            lock (captureSync)
                for (var i = 0; i + 4 <= buffer.Length; i += 4)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer.Slice(i, 4))));
        };
        recorder.StartRecording(); await Task.Delay(400);
        double baseline; lock (captureSync) { baseline = peak; peak = 0; }
        var voice = SpeechAudio.GetVoices().First();
        var fixture = voice.Label.Contains("(en-") ? "This speech must reach the selected virtual input. " : "这段测试语音必须进入指定的虚拟输入。";
        var settings = new Settings(VoiceId: voice.Id, Volume: 0.3f, OutputDeviceIds: [selected.Id]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var speech = new SpeechAudio(); var audio = await speech.SynthesizeAsync(string.Concat(Enumerable.Repeat(fixture, 3)), settings, timeout.Token);
        using var group = speech.CreatePlayback(audio, settings); group.Play(); await Task.Delay(2500, timeout.Token); group.Stop(); recorder.StopRecording();
        double received; lock (captureSync) received = peak;
        log.WriteLine($"Endpoint: {selected.Label}; baseline peak: {baseline:F6}; speech peak: {received:F6}; warning: {group.Warning ?? "none"}");
        Assert.True(received > 0.001, "The actual selected render endpoint's loopback received no non-silent samples.");
    }
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task ConfiguredFactoryCompletesAllEndpointsAndReportsRealDefaultFallback()
    {
        var ids = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray();
        Assert.Equal(2, ids.Length);
        var voice = SpeechAudio.GetVoices().First(); var settings = new Settings(VoiceId: voice.Id, OutputDeviceIds: ids, Volume: 0.1f);
        var speech = new SpeechAudio(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var fixture = voice.Label.Contains("(en-") ? "All outputs finish this short test." : "所有输出完成这次测试。";
        var audio = await speech.SynthesizeAsync(fixture, settings, timeout.Token);
        using var group = speech.CreatePlayback(audio, settings);
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        group.Finished += error => completion.TrySetResult(error); group.Play();
        Assert.Null(await completion.Task.WaitAsync(timeout.Token)); Assert.False(audio.CanRead); Assert.Null(group.Warning);
        audio = await speech.SynthesizeAsync(fixture, settings, timeout.Token);
        using var fallback = speech.CreatePlayback(audio, settings with { OutputDeviceIds = ["SeleVox-nonexistent-fixture-endpoint"] });
        Assert.Contains("回退", fallback.Warning); fallback.Play(); await Task.Delay(250, timeout.Token); fallback.Stop();
    }
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task ExplicitEndpointAndMultiEndpointPlaybackUseIndependentReaders()
    {
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).OrderBy(d => d.Label.Contains("Realtek") ? 0 : 1).ToArray();
        Assert.NotEmpty(devices); log.WriteLine("Active render devices: " + string.Join("; ", devices.Select(d => d.Label)));
        using var enumerator = new MMDeviceEnumerator();
        var voice = SpeechAudio.GetVoices().First();
        var text = string.Concat(Enumerable.Repeat(voice.Label.Contains("(en-") ? "This is a multiple output audio test. " : "这是多设备播放测试。", 20));
        var speech = new SpeechAudio(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var source = await speech.SynthesizeAsync(text, new(VoiceId: voice.Id), timeout.Token);
        using var copy = new MemoryStream(); await source.CopyToAsync(copy, timeout.Token); source.Dispose(); var bytes = copy.ToArray();
        using (var single = new WasapiPlayback(new MemoryStream(bytes, false), 0.1f, enumerator.GetDevice(devices[0].Id)))
        {
            Assert.Equal(devices[0].Id, single.DeviceId); single.Play(); await Task.Delay(350, timeout.Token); Assert.True(single.SourcePosition > 0); single.Stop();
        }
        Assert.True(devices.Length >= 2, "At least two active render endpoints are required for this real multiple output test.");
        var first = new WasapiPlayback(new MemoryStream(bytes, false), 0.1f, enumerator.GetDevice(devices[0].Id));
        var second = new WasapiPlayback(new MemoryStream(bytes, false), 0.1f, enumerator.GetDevice(devices[1].Id));
        using var group = new MultiOutputPlayback([first, second]); group.Play(); await Task.Delay(450, timeout.Token);
        group.Pause(); await Task.Delay(150, timeout.Token); var positions = (first.SourcePosition, second.SourcePosition);
        Assert.True(positions.Item1 > 0); Assert.True(positions.Item2 > 0); await Task.Delay(250, timeout.Token);
        Assert.Equal(positions, (first.SourcePosition, second.SourcePosition)); group.Play(); await Task.Delay(350, timeout.Token);
        Assert.True(first.SourcePosition > positions.Item1); Assert.True(second.SourcePosition > positions.Item2); group.Stop();
        log.WriteLine($"Real endpoints tested: {devices[0].Label}; {devices[1].Label}. Source cursor pause/resume passed; not an acoustic or virtual microphone reception assertion.");
        using var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in captures) { using (device) log.WriteLine("Active capture: " + device.FriendlyName); }
    }
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task LocalVoiceProducesDecodableWaveAndWasapiPauseKeepsPosition()
    {
        Assert.NotEmpty(SpeechAudio.GetVoices());
        var speech = new SpeechAudio();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = string.Concat(Enumerable.Repeat("这是本地音频测试，检查暂停继续和停止。", 15));
        var audio = await speech.SynthesizeAsync(text, new(), timeout.Token);
        using var playback = new WasapiPlayback(audio, 0.15f);
        playback.Play();
        await Task.Delay(450, timeout.Token);
        playback.Pause();
        await Task.Delay(150, timeout.Token);
        var pausedPosition = playback.SourcePosition;
        Assert.True(pausedPosition > 0);
        await Task.Delay(300, timeout.Token);
        Assert.Equal(pausedPosition, playback.SourcePosition);
        playback.Play();
        await Task.Delay(350, timeout.Token);
        Assert.True(playback.SourcePosition > pausedPosition);
        playback.Stop(); playback.Dispose();
        Assert.False(audio.CanRead);
    }

    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task RealSynthesisHonorsSelectedVoiceAndRateAndReturnsWaveContainer()
    {
        var voice = SpeechAudio.GetVoices().First();
        var speech = new SpeechAudio();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // An English voice can return an empty WAVE for Chinese text. Use a fixture it can speak.
        var text = voice.Label.Contains("(en-", StringComparison.OrdinalIgnoreCase)
            ? "This is a fixed test sentence for comparing the speaking rate."
            : "这是用于比较语速的固定测试句子。";
        using var slow = await speech.SynthesizeAsync(text, new(voice.Id, 0.5), timeout.Token);
        using var fast = await speech.SynthesizeAsync(text, new(voice.Id, 2), timeout.Token);
        using var slowReader = new WaveFileReader(slow);
        using var fastReader = new WaveFileReader(fast);
        Assert.Equal(WaveFormatEncoding.Pcm, slowReader.WaveFormat.Encoding);
        Assert.True(slowReader.TotalTime > fastReader.TotalTime,
            $"Voice: {voice.Label}; slow: {slowReader.TotalTime}; fast: {fastReader.TotalTime}");
    }
}

public sealed class VoicemeeterAudioFactAttribute : FactAttribute
{
    public VoicemeeterAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_VOICEMEETER_TESTS") != "1")
            Skip = "Set SELEVOX_VOICEMEETER_TESTS=1 with Voicemeeter Input installed to test endpoint loopback.";
    }
}

public sealed class WindowsAudioFactAttribute : FactAttribute
{
    public WindowsAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_AUDIO_TESTS") != "1")
            Skip = "Set SELEVOX_AUDIO_TESTS=1 to emit fixture speech and test the default output device.";
    }
}
