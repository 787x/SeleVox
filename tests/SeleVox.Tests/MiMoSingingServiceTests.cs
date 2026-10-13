using System.Diagnostics;
using NAudio.CoreAudioApi;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

public sealed class MiMoSingingServiceTests(ITestOutputHelper log)
{
    [MiMoStreamingServiceFact, Trait("Category", "MiMoSingingService")]
    public async Task RealShortMediumLongSingingAndOrdinarySpeech()
    {
        const string lyrics = "晨光轻轻落在窗前，微风带来花的笑脸。\n我们走过青色田野，把小小心愿交给蓝天。\n";
        (string Id, string Text, bool Singing)[] cases =
        [("ordinary", "这是固定的普通流式朗读测试，检查首声和完整播放。", false),
            ("short", lyrics, true), ("medium", lyrics + "夜色慢慢铺满山间，星光点亮远方屋檐。\n我们唱着明天的歌，让每一步都温暖一点。\n", true),
            ("long", lyrics + "白云轻轻越过山岗，河水追着阳光流淌。\n小路弯弯连着村庄，朋友笑着站在身旁。\n" +
                "晚霞慢慢染红天边，归鸟带来新的思念。\n我们守着安静灯火，让远方也靠近一点。\n" +
                "夜色慢慢铺满山间，星光点亮远方屋檐。\n我们唱着明天的歌，让每一步都温暖一点。\n", true)];
        using var total = new CancellationTokenSource(TimeSpan.FromMinutes(12)); var failures = new List<string>();
        var selected = Environment.GetEnvironmentVariable("SELEVOX_SINGING_CASE");
        Assert.True(string.IsNullOrEmpty(selected) || cases.Any(c => c.Id == selected));
        foreach (var item in cases.Where(c => string.IsNullOrEmpty(selected) || c.Id == selected))
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(total.Token); budget.CancelAfter(TimeSpan.FromMinutes(6));
            using var buffer = new StreamingPcmBuffer(); using var enumerator = new MMDeviceEnumerator();
            using var player = new StreamingWasapiPlayback(buffer.CreateReader(), .1f, enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
            var settings = new Settings(Provider: TtsProvider.MiMo, MiMoOverallStyles: item.Singing ? ["唱歌"] : []);
            var metrics = new MiMoStreamingMetrics(); using var process = Process.GetCurrentProcess(); process.Refresh();
            var baseline = process.PrivateMemorySize64; var peak = baseline; var heapPeak = GC.GetTotalMemory(false);
            using var sampleCancellation = new CancellationTokenSource();
            var sampling = Task.Run(async () =>
            {
                while (!sampleCancellation.IsCancellationRequested)
                {
                    process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64); heapPeak = Math.Max(heapPeak, GC.GetTotalMemory(false));
                    var directory = Environment.GetEnvironmentVariable("SELEVOX_SINGING_DIAGNOSTICS_DIR");
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        File.WriteAllText(Path.Combine(directory, item.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(new
                        { elapsed = metrics.ElapsedMilliseconds, headers = metrics.ResponseHeadersMilliseconds, firstEvent = metrics.FirstCompleteAudioEventMilliseconds,
                            firstPcm = metrics.FirstAudioMilliseconds, maxEvent = metrics.MaxEventBytes, pcm = metrics.AudioBytes,
                            produced = buffer.ProducedBytes, consumed = player.SourcePosition, complete = buffer.IsComplete, phase = metrics.TimeoutStage,
                            peakPrivate = peak, heapPeak }));
                    }
                    await Task.Delay(100, sampleCancellation.Token);
                }
            });
            var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Finished += error => finished.TrySetResult(error);
            var production = Task.Run(async () =>
            { try { await new MiMoSpeech().SynthesizeStreamingAsync(item.Text, settings, buffer, budget.Token, metrics); } finally { buffer.Complete(); } });
            double? play = null;
            try
            {
                await buffer.WaitForReadyAsync(budget.Token); play = metrics.ElapsedMilliseconds; player.Play();
                await production; Assert.Null(await finished.Task.WaitAsync(budget.Token));
                Assert.Equal(buffer.ProducedBytes, player.SourcePosition); Assert.Equal(1, metrics.Requests);
                log.WriteLine($"{item.Id}: complete PCM drain on actual WASAPI; first playback call={play:F0}ms; duration={buffer.ProducedBytes / 48000.0:F1}s.");
            }
            catch (Exception error) when (error is UserFeedbackException or OperationCanceledException)
            { failures.Add(item.Id); log.WriteLine($"{item.Id}: failure stage={metrics.TimeoutStage ?? "receive/playback"}; {(error is UserFeedbackException ? error.Message : "Bounded test cancellation.")}"); }
            finally
            {
                budget.Cancel(); player.Stop();
                try { await production; } catch (Exception error) when (error is UserFeedbackException or OperationCanceledException) { }
                sampleCancellation.Cancel(); try { await sampling; } catch (OperationCanceledException) { }
                log.WriteLine($"{item.Id}: elapsed={metrics.ElapsedMilliseconds:F0}ms, request pipeline complete={metrics.NetworkCompleteMilliseconds:F0}ms, headers={metrics.ResponseHeadersMilliseconds:F0}ms, first body byte={metrics.FirstBodyByteMilliseconds:F0}ms, first complete event={metrics.FirstCompleteAudioEventMilliseconds:F0}ms, first valid PCM={metrics.FirstAudioMilliseconds:F0}ms, max SSE event={metrics.MaxEventBytes} bytes, PCM={metrics.AudioBytes} bytes, network reads={metrics.NetworkReadMilliseconds:F0}ms, parse/decode={metrics.ParseMilliseconds:F0}ms, ring writes/backpressure={metrics.BufferWriteMilliseconds:F0}ms, ring peak={buffer.PeakBufferedBytes}/{buffer.Capacity}, private memory baseline={baseline}, sampled peak={peak}, delta={peak - baseline}, heap peak={heapPeak}, timeout={metrics.TimeoutStage ?? "none"}.");
            }
        }
        Assert.Empty(failures);
    }
}
