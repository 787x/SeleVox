using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

public sealed class MiMoStreamingServiceTests(ITestOutputHelper log)
{
    [MiMoStreamingServiceFact, Trait("Category", "MiMoStreamingService")]
    public async Task RealSessionDualOutputsPauseReplaceStopAndRevoke()
    {
        var ids = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray(); Assert.Equal(2, ids.Length);
        var backend = new StreamingAudioTests.RealBackend(); using var session = new ReadingSession(backend, backend);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var settings = new Settings(Provider: TtsProvider.MiMo, Volume: .1f, OutputDeviceIds: ids, AllowAutomaticMiMo: true,
            MiMoOverallStyles: ["温柔", "清亮"]);
        var longText = string.Concat(Enumerable.Repeat("这是固定的取消与替换流式测试。", 70));
        async Task WaitForPlaying()
        {
            while (session.Status.State != ReadingState.Playing)
            { Assert.True(session.Status.State != ReadingState.Error, session.Status.Message); await Task.Delay(10, timeout.Token); }
        }
        var first = session.ReadAsync(_ => Task.FromResult(longText), settings); await WaitForPlaying(); await Task.Delay(250, timeout.Token);
        var firstPlayers = backend.Players.Take(2).ToArray(); Assert.All(firstPlayers, p => Assert.True(p.SourcePosition > 0));
        session.TogglePause(); await Task.Delay(150, timeout.Token); var positions = firstPlayers.Select(p => p.SourcePosition).ToArray(); await Task.Delay(250, timeout.Token);
        Assert.Equal(positions, firstPlayers.Select(p => p.SourcePosition).ToArray()); session.TogglePause(); await Task.Delay(200, timeout.Token);
        Assert.All(firstPlayers.Select((p, i) => p.SourcePosition > positions[i]), Assert.True);
        var second = session.ReadAsync(_ => Task.FromResult("这是替换后的固定中文流式测试。"), settings); await first.WaitAsync(timeout.Token);
        await WaitForPlaying(); await second.WaitAsync(timeout.Token); Assert.Equal(ReadingState.Idle, session.Status.State);
        var automatic = session.ReadTextAsync(longText, settings); await WaitForPlaying(); Assert.False(backend.Buffer!.IsComplete);
        session.CancelPendingAutomatic(); var accepted = backend.Buffer.ProducedBytes; await Task.Delay(250, timeout.Token);
        Assert.Equal(accepted, backend.Buffer.ProducedBytes); await automatic.WaitAsync(timeout.Token); Assert.Contains("已取消", session.Status.Message);
        var stopped = session.ReadAsync(_ => Task.FromResult(longText), settings); await WaitForPlaying(); session.Stop();
        await stopped.WaitAsync(timeout.Token); Assert.Equal(ReadingState.Idle, session.Status.State);
        log.WriteLine("Real MiMo + ReadingSession + dual WASAPI: both cursors consumed PCM; pause/resume kept position; replacement cancelled old producer; revocation stopped append and drained accepted audio; Stop completed. Fixed fixture text only, no selection capture.");
    }
    [MiMoStreamingServiceFact, Trait("Category", "MiMoStreamingService")]
    public async Task RealChineseEnglishAndLongTextLatencyComparison()
    {
        // Fixed synthetic text only; the long selection exceeds a single MiMo request's byte budget.
        var text = string.Concat(Enumerable.Repeat("这是流式朗读延迟对比测试，按顺序检查长文本。", 50));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var settings = new Settings(Provider: TtsProvider.MiMo, Volume: .12f);
        var clock = Stopwatch.StartNew();
        using (var wave = await new MiMoSpeech().SynthesizeAsync(text, settings with { MiMoStreaming = false }, timeout.Token))
        {
            var fullSynthesis = clock.Elapsed.TotalMilliseconds;
            using var player = new WasapiPlayback(wave, settings.Volume);
            using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var anchor = clock.Elapsed.TotalMilliseconds; var observed = await CaptureFirstRenderSampleAsync(device, player, timeout.Token); player.Stop();
            log.WriteLine($"Nonstream long: complete synthesis={fullSynthesis:F0} ms; Play={anchor + observed.Play:F0} ms; first non-silent endpoint loopback <= {anchor + observed.FirstSample:F0} ms. Remaining baseline audio stopped after latency observation.");
        }
        await RealStreaming(text, settings, timeout.Token);
        await RealStreaming("这是固定的中文流式语音测试。", settings, timeout.Token);
        await RealStreaming("This is a fixed English streaming speech test.", settings with { MiMoVoice = "Mia" }, timeout.Token);
    }
    private async Task RealStreaming(string text, Settings settings, CancellationToken cancellation)
    {
        using var buffer = new StreamingPcmBuffer(); using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var player = new StreamingWasapiPlayback(buffer.CreateReader(), settings.Volume, enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        var metrics = new MiMoStreamingMetrics();
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var production = new MiMoSpeech().SynthesizeStreamingAsync(text, settings, buffer, producerCancellation.Token, metrics);
        try
        {
            await buffer.WaitForReadyAsync(cancellation);
            var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously); player.Finished += error => finished.TrySetResult(error);
            var silenceBefore = player.SilenceBytes;
            var anchor = metrics.ElapsedMilliseconds; var observed = await CaptureFirstRenderSampleAsync(device, player, cancellation);
            var playTime = anchor + observed.Play; var firstRender = anchor + observed.FirstSample;
            Assert.False(production.IsFaulted, "Production must not fail while waiting for first output.");
            await production; buffer.Complete();
            Assert.Null(await finished.Task.WaitAsync(cancellation)); Assert.Equal(buffer.ProducedBytes, player.SourcePosition);
            log.WriteLine($"Stream {settings.MiMoVoice}, segments={metrics.Requests}: first audio block={metrics.FirstAudioMilliseconds:F0} ms; Play={playTime:F0} ms; first non-silent endpoint loopback <= {firstRender:F0} ms; network complete={metrics.NetworkCompleteMilliseconds:F0} ms; playback complete={metrics.ElapsedMilliseconds:F0} ms; PCM={buffer.ProducedBytes} bytes; inserted silence after Play={player.SilenceBytes - silenceBefore} bytes; ring peak={buffer.PeakBufferedBytes}/{buffer.Capacity} bytes.");
            if (metrics.Requests > 1) Assert.True(playTime < metrics.NetworkCompleteMilliseconds);
        }
        finally
        {
            // All exceptional paths must also stop the producer; its bounded ring can otherwise be awaiting consumption.
            producerCancellation.Cancel();
            try { await production; } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or UserFeedbackException) { }
        }
    }
    private static async Task<(double Play, double FirstSample)> CaptureFirstRenderSampleAsync(MMDevice device, IAudioPlayback playback, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew(); var first = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recorder = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
        recorder.DataAvailable += (samples, _, _, _) =>
        {
            for (var i = 0; i + 4 <= samples.Length; i += 4)
                if (Math.Abs(BitConverter.ToSingle(samples.Slice(i, 4))) > .001) { first.TrySetResult(clock.Elapsed.TotalMilliseconds); break; }
        };
        recorder.StartRecording(); var play = clock.Elapsed.TotalMilliseconds; playback.Play();
        var elapsed = await first.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation); recorder.StopRecording(); return (play, elapsed);
    }
}
public sealed class MiMoStreamingServiceFactAttribute : FactAttribute
{
    public MiMoStreamingServiceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_MIMO_STREAM_TESTS") != "1")
            Skip = "Explicitly enable SELEVOX_MIMO_STREAM_TESTS=1 for billable MiMo calls and actual endpoint playback.";
        else
        {
            try { if (AzureCredentials.MiMo().Read() is null) Skip = "No saved MiMo credential; real service not verified."; }
            catch (UserFeedbackException) { Skip = "MiMo credential unavailable; real service not verified."; }
        }
    }
}
