using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

// Explicit opt-in; saved Windows credential; fixed synthetic text only. No content/key logs.
public sealed class MiMoStyleServiceTests(ITestOutputHelper log)
{
    [MiMoStyleServiceFact, Trait("Category", "MiMoStyleService")]
    public async Task RealFixedStyleMatrixBothProtocolsAndLongText()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var baseline = new Settings(Provider: TtsProvider.MiMo, MiMoVoice: "冰糖", MiMoModel: "mimo-v2.5-tts", Volume: .12f);
        (string Id, Settings Settings, string Text)[] cases =
        [
            ("baseline", baseline, "今天天气真不错，我们一起去公园走走。"),
            ("instruction-only", baseline with { MiMoStyle = "(上海话)" }, "今天天气真不错，我们一起去公园走走。"),
            ("custom-tag", baseline with { MiMoOverallStyles = ["上海话"] }, "今天天气真不错，我们一起去公园走走。"),
            ("combined-tags", baseline with { MiMoOverallStyles = ["温柔", "清亮"] }, "今天天气真不错，我们一起去公园走走。"),
            ("instruction-and-tags", baseline with { MiMoStyle = "请以自然、轻快的语气朗读。", MiMoOverallStyles = ["温柔", "清亮"] }, "今天天气真不错，我们一起去公园走走。"),
            ("singing", baseline with { MiMoOverallStyles = ["唱歌"] }, "小风吹过山坡，星光落进小河。你我轻轻唱着，明天又是晴天。"),
            ("long-tags", baseline with { MiMoOverallStyles = ["温柔", "上海话"] }, string.Concat(Enumerable.Repeat("这是固定的长文本风格测试。春风吹过公园，大家慢慢散步。", 42)))
        ];
        var filter = Environment.GetEnvironmentVariable("SELEVOX_STYLE_CASE");
        Assert.True(string.IsNullOrEmpty(filter) || cases.Any(c => c.Id == filter), "Unknown fixed style case.");
        var failures = new List<string>();
        foreach (var item in cases.Where(c => string.IsNullOrEmpty(filter) || c.Id == filter))
            foreach (var streaming in new[] { false, true })
            {
                try
                {
                    using var audio = streaming ? await StreamWave(item.Text, item.Settings, timeout.Token)
                        : await new MiMoSpeech().SynthesizeAsync(item.Text, item.Settings, timeout.Token);
                    using var reader = new WaveFileReader(audio); Assert.True(reader.Length > 0);
                    log.WriteLine($"{item.Id}, stream={streaming}: valid WAV, PCM bytes={reader.Length}, duration={reader.TotalTime.TotalSeconds:F1}s; style perception not verified.");
                    var directory = Environment.GetEnvironmentVariable("SELEVOX_STYLE_AUDIO_DIR");
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory); audio.Position = 0;
                        using var file = File.Create(Path.Combine(directory, item.Id + (streaming ? "-stream.wav" : "-wav.wav")));
                        await audio.CopyToAsync(file, timeout.Token);
                    }
                    if (Environment.GetEnvironmentVariable("SELEVOX_AUDIO_TESTS") == "1")
                    {
                        audio.Position = 0; using var playback = AudioOutputs.CreatePlayback(audio, item.Settings);
                        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        playback.Finished += error => finished.TrySetResult(error); playback.Play();
                        if (item.Id == "long-tags") { await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token); playback.Stop(); }
                        else Assert.Null(await finished.Task.WaitAsync(TimeSpan.FromSeconds(60), timeout.Token));
                        log.WriteLine($"{item.Id}, stream={streaming}: actual WASAPI playback {(item.Id == "long-tags" ? "first 2 seconds then Stop" : "completed")}; no human listening assertion.");
                    }
                }
                catch (UserFeedbackException error)
                { log.WriteLine($"{item.Id}, stream={streaming}: {error.Message}"); failures.Add($"{item.Id}, stream={streaming}: {error.Message}"); }
            }
        Assert.Empty(failures);
    }
    private async Task<Stream> StreamWave(string text, Settings settings, CancellationToken cancellation)
    {
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var metrics = new MiMoStreamingMetrics();
        using var bytes = new MemoryStream();
        using var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(bytes), new WaveFormat(24000, 16, 1));
        var consumer = Task.Run(async () =>
        {
            var block = new byte[8192];
            while (true)
            {
                pendingCancellation.Token.ThrowIfCancellationRequested();
                var before = reader.ConsumedBytes;
                reader.Read(block);
                // The playback reader returns silence during live underflow. Capture only source PCM.
                var read = checked((int)(reader.ConsumedBytes - before));
                if (read > 0) writer.Write(block, 0, read);
                else if (buffer.IsComplete) break;
                else await Task.Delay(5, pendingCancellation.Token);
            }
        }, pendingCancellation.Token);
        try
        {
            await new MiMoSpeech().SynthesizeStreamingAsync(text, settings, buffer, pendingCancellation.Token, metrics);
            buffer.Complete(); await consumer; writer.Flush();
            Assert.Equal(buffer.ProducedBytes, writer.Length);
            log.WriteLine($"Streaming requests={metrics.Requests}, PCM bytes={buffer.ProducedBytes}.");
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 3000) Assert.True(metrics.Requests > 1);
            return new MemoryStream(bytes.ToArray());
        }
        finally { pendingCancellation.Cancel(); try { await consumer; } catch (OperationCanceledException) { } }
    }
}

public sealed class MiMoStyleServiceFactAttribute : FactAttribute
{
    public MiMoStyleServiceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_MIMO_STYLE_TESTS") != "1")
            Skip = "Enable SELEVOX_MIMO_STYLE_TESTS=1 for bounded real MiMo style tests.";
        else
        {
            try { if (AzureCredentials.MiMo().Read() is null) Skip = "No saved MiMo credential; real style service not verified."; }
            catch (UserFeedbackException) { Skip = "MiMo credential unavailable; real style service not verified."; }
        }
    }
}
