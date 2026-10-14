using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class VirtualMicrophoneWindowsTests(ITestOutputHelper log)
{
    internal static VirtualMicrophoneRoute ActualRoute()
    {
        var endpoints = VirtualMicrophones.Scan();
        var capture = endpoints.First(d => d.Flow == DataFlow.Capture && d.State == DeviceState.Active);
        var render = endpoints.Last(d => d.Flow == DataFlow.Render && d.State == DeviceState.Active && d.DriverInstanceId == capture.DriverInstanceId);
        return new(render.Id, capture.Id, render.DriverInstanceId, true);
    }
    private static Settings DualSettings(VirtualMicrophoneRoute route)
    {
        var physical = AudioOutputs.GetDevices().First(d => d.Label.Contains("Realtek"));
        return new(OutputDeviceIds: [physical.Id], Volume: .1f) { VirtualMicrophoneEnabled = true, VirtualMicrophone = route };
    }
    internal sealed class Capture : IDisposable
    {
        private readonly MMDevice device;
        private readonly WasapiRecorder recorder;
        private readonly object gate = new();
        private readonly List<float> samples = [];
        private readonly TaskCompletionSource<Exception?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Capture(string id)
        {
            using var enumerator = new MMDeviceEnumerator(); device = enumerator.GetDevice(id);
            recorder = new WasapiRecorderBuilder().WithDevice(device).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)).Build();
            recorder.DataAvailable += (data, _, _, _) => { lock (gate) for (var i = 0; i + 4 <= data.Length; i += 4) samples.Add(BitConverter.ToSingle(data.Slice(i, 4))); };
            recorder.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
            recorder.StartRecording();
        }
        public async Task<double> Rms()
        {
            await Task.Delay(250); int start; lock (gate) start = samples.Count;
            await Task.Delay(300); lock (gate) return samples.Count == start ? 0 : Math.Sqrt(samples.Skip(start).Average(x => (double)x * x));
        }
        public void Dispose() { recorder.StopRecording(); stopped.Task.Wait(TimeSpan.FromSeconds(3)); recorder.Dispose(); device.Dispose(); }
    }
    [VirtualMicFact] public async Task RealPhysicalOutputAndVirtualCapturePauseResumeStopReplacementAndDisable()
    {
        var route = ActualRoute(); var settings = DualSettings(route);
        Assert.Equal(2, VirtualMicrophones.Resolve(settings).OutputDeviceIds!.Length);
        using var capture = new Capture(route.CaptureId);
        var backend = new FixedSpeech(); using var reading = new ReadingSession(backend, new SpeechAudio());
        await reading.ReadAsync(_ => Task.FromResult("fixed PCM"), settings);
        Assert.Equal(ReadingState.Playing, reading.Status.State); Assert.Null(reading.Status.Message.Contains("跳过") ? "unavailable" : null);
        Assert.True(await capture.Rms() > .002);
        reading.TogglePause(); Assert.Equal(ReadingState.Paused, reading.Status.State); Assert.True(await capture.Rms() < .0005);
        reading.TogglePause(); Assert.True(await capture.Rms() > .002);
        await reading.ReadAsync(_ => Task.FromResult("replacement PCM"), settings);
        Assert.True(await capture.Rms() > .002);
        reading.Stop(); Assert.True(await capture.Rms() < .0005);
        await reading.ReadAsync(_ => Task.FromResult("physical only PCM"), settings with { VirtualMicrophoneEnabled = false });
        Assert.Equal(ReadingState.Playing, reading.Status.State); Assert.True(await capture.Rms() < .0005);
        reading.Stop();
        log.WriteLine("Realtek speakers + VB-CABLE: Capture RMS positive during playback/resume/replacement, silent after pause/Stop and during physical-only playback. Headphones unavailable in this run.");
    }
    [VirtualMicFact] public async Task RealStreaming24KhzDualOutputsCapturePauseResumeStop()
    {
        var route = ActualRoute(); var settings = DualSettings(route);
        using var capture = new Capture(route.CaptureId);
        using var buffer = new StreamingPcmBuffer(capacity: 24000 * 2 * 12);
        using var group = AudioOutputs.CreateStreamingPlayback(buffer, settings);
        var pcm = StreamingAudioTests.Tone(9000); await buffer.WriteAsync(pcm, default); buffer.Complete();
        Assert.Null(group.Warning); group.Play(); Assert.True(await capture.Rms() > .001);
        group.Pause(); Assert.True(await capture.Rms() < .0005);
        group.Play(); Assert.True(await capture.Rms() > .001);
        group.Stop(); Assert.True(await capture.Rms() < .0005);
        log.WriteLine("Real 24 kHz mono PCM through streaming factory: dual outputs and actual Capture pause/resume/Stop verified. No live MiMo network request.");
    }
    [VirtualMicFact] public async Task ThirdPartyFfmpegDirectShowReceivesFixedPcmOnRealCapture()
    {
        var executable = Environment.GetEnvironmentVariable("SELEVOX_FFMPEG") ?? @"C:\ffmpeg\bin\ffmpeg.exe";
        Assert.True(File.Exists(executable), "Provide SELEVOX_FFMPEG for this third-party recording test.");
        var route = ActualRoute(); var capture = VirtualMicrophones.Scan().Single(d => d.Id == route.CaptureId);
        var path = Path.Combine(Path.GetTempPath(), "SeleVox-pr17-" + Guid.NewGuid().ToString("N") + ".wav");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-f", "dshow", "-i", "audio=" + capture.Label, "-t", "5", "-ac", "1", "-ar", "24000", "-c:a", "pcm_s16le", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            await Task.Delay(900);
            using var group = AudioOutputs.CreatePlayback(VirtualMicrophoneTest.Tone(), new(OutputDeviceIds: [route.RenderId], Volume: 1, StartupSilenceMilliseconds: 0) { StrictOutputDevices = true });
            group.Play(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); Assert.Equal(0, process.ExitCode);
            using var reader = new WaveFileReader(path); var provider = reader.ToSampleProvider(); var values = new float[reader.Length / 2];
            var count = provider.Read(values); Assert.True(count > 72000);
            var score = VirtualMicrophoneTest.Correlation(values[..count], 24000); Assert.True(score >= .6, $"Third-party PCM correlation={score:F4}");
            log.WriteLine($"FFmpeg DirectShow selected real Capture {capture.Label}; recorded {count / 24000d:F2} seconds; normalized fixed PCM correlation={score:F4}. Not a Discord/OBS UI test.");
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } await errors; await stdout; if (File.Exists(path)) File.Delete(path); }
    }
    [VirtualMicFact] public async Task IncrementalAnswerSentencesUseSameRealVirtualRoute()
    {
        var route = ActualRoute(); var settings = DualSettings(route) with { LlmModel = "controlled-fixture", LlmReadingStrategy = AnswerReadingStrategy.Incremental };
        using var capture = new Capture(route.CaptureId);
        using var reading = new ReadingSession(new FixedSpeech(1), new SpeechAudio()); var sentences = 0;
        using var question = new QuestionSession((_, _, _, delta, _) => { const string answer = "First answer sentence. Second answer sentence."; delta(answer); return Task.FromResult(answer); },
            (text, s) => reading.ReadSentenceAsync(text, s, default), reading.Stop,
            (text, s, token) => { sentences++; return reading.ReadSentenceAsync(text, s, token); });
        var task = question.AskAsync("fixed question", settings, "fixed-local-test-key");
        await Task.Delay(800); Assert.True(await capture.Rms() > .002);
        await task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.True(sentences >= 2);
        Assert.Equal(QuestionState.Completed, question.Snapshot.State); Assert.True(await capture.Rms() < .0005);
        log.WriteLine("Controlled incremental LLM answer, actual ReadingSession + multi-output WASAPI: both sentences reached real Capture and ended without residual injection. No live LLM request.");
    }
    private sealed class FixedSpeech(int repeats = 5) : ISpeechSynthesis
    {
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        {
            using var tone = VirtualMicrophoneTest.Tone(); using var reader = new WaveFileReader(tone);
            var pcm = new byte[reader.Length]; reader.ReadExactly(pcm);
            var memory = new MemoryStream();
            using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), reader.WaveFormat))
                for (var i = 0; i < repeats; i++) writer.Write(pcm);
            memory.Position = 0; return Task.FromResult<Stream>(memory);
        }
    }
}
