using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

// Only synthetic audio is captured, in memory. No user speech or credentials are saved.
[Collection("Audio onset diagnostic")]
public sealed class AudioOnsetTests(ITestOutputHelper log)
{
    internal static byte[] Marker(int milliseconds = 350)
    {
        var pcm = new byte[milliseconds * 48];
        for (var i = 0; i < pcm.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(6000 * Signal(i / 24000d)));
        return pcm;
    }
    private static double Signal(double seconds) => Math.Cos(2 * Math.PI *
        (seconds < .04 ? 997 : seconds < .08 ? 1801 : seconds < .12 ? 2609 : 433) * seconds);
    internal static MemoryStream Wave(byte[] pcm)
    {
        var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(24000, 16, 1)))
            writer.Write(pcm);
        memory.Position = 0; return memory;
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(8192)]
    public async Task ControlledMiMoSseAndCachedWavePreserveEverySampleIncludingFirst(int fragment)
    {
        var pcm = Marker(); Assert.Equal(6000, BinaryPrimitives.ReadInt16LittleEndian(pcm));
        var events = MiMoStreamingTests.Event(pcm[..1]) + MiMoStreamingTests.Event(pcm[1..103]) +
            MiMoStreamingTests.Event(pcm[103..]) + "data: [DONE]\n\n";
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(
            MiMoStreamingTests.Response(new MiMoStreamingTests.FragmentStream(Encoding.UTF8.GetBytes(events), fragment)))));
        using var buffer = new StreamingPcmBuffer(); using var a = buffer.CreateReader(); using var b = buffer.CreateReader();
        await new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("第一句开头完整。", new(), buffer, default);
        buffer.Complete();
        foreach (var reader in new[] { a, b })
        {
            using var actual = new MemoryStream(); var block = new byte[262]; int count;
            while ((count = reader.Read(block)) != 0) actual.Write(block, 0, count);
            Assert.Equal(pcm, actual.ToArray()); Assert.Equal(pcm.Length, reader.ConsumedBytes);
        }
        using var wave = new WaveFileReader(Wave(pcm));
        var cached = new byte[pcm.Length]; Assert.Equal(cached.Length, wave.Read(cached)); Assert.Equal(pcm, cached);
        using var cachedContainer = Wave(pcm);
        using var completeHttp = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(cachedContainer.ToArray()) } } } } })) })));
        using var completeAudio = await new MiMoSpeech(completeHttp, () => "fixture").SynthesizeAsync("第一句开头完整。", new(), default);
        using var completeWave = new WaveFileReader(completeAudio);
        var completePcm = new byte[pcm.Length]; Assert.Equal(pcm.Length, completeWave.Read(completePcm)); Assert.Equal(pcm, completePcm);
    }

    [Fact]
    public void EveryTokenBoundaryPreservesFirstSentenceAndFollowingOrder()
    {
        const string text = "第一句开头🌟e\u0301必须完整。第二句！第三句余文";
        for (var split = 0; split <= text.Length; split++)
        {
            var buffer = new AnswerSentenceBuffer();
            var sentences = buffer.Append(text[..split]).Concat(buffer.Append(text[split..])).Concat(buffer.Append("", true)).ToArray();
            Assert.Equal(new[] { "第一句开头🌟e\u0301必须完整。", "第二句！", "第三句余文" }, sentences);
        }
    }

    [Fact]
    public void LoopbackAnalyzerDistinguishesClippedOnsetFromLaterSignalAndGain()
    {
        var recorded = new float[24000];
        for (var i = 0; i < 16800; i++) recorded[2400 + i] = (float)(.03 * Signal(i / 48000d));
        var intact = Analyze(recorded);
        Assert.InRange(intact.Correlation, .99, 1.001); Assert.Equal(2400, intact.Lag);
        Assert.InRange(intact.OnsetRatio, .95, 1.05);
        Array.Clear(recorded, 2400, 960);
        var clipped = Analyze(recorded);
        Assert.Equal(intact.Lag, clipped.Lag); Assert.Equal(0, clipped.OnsetRatio);
        Assert.True(clipped.Correlation > .99); // Recognizable tail alone must not prove intact onset.
    }

    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task RealStreamingReplacementDuringStartupCancelsOldProducerSingleAndDual()
    {
        var ids = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray();
        Assert.Equal(2, ids.Length);
        foreach (var outputs in new[] { new[] { Settings.DefaultOutput }, ids })
        {
            var sources = new List<MiMoStreamingTests.DelayedStream>();
            using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) =>
            {
                var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Marker(1500)), "data: [DONE]\n\n");
                sources.Add(source); return Task.FromResult(MiMoStreamingTests.Response(source));
            }));
            var backend = new StreamingAudioTests.RealBackend(http); using var session = new ReadingSession(backend, backend);
            var settings = new Settings(Provider: TtsProvider.MiMo, Volume: .1f, OutputDeviceIds: outputs);
            var old = session.ReadAsync(_ => Task.FromResult("旧固定测试。"), settings);
            await Pr9Tests.Until(() => session.Status.State == ReadingState.Playing);
            var oldBuffer = backend.Buffer!; var oldPlayers = backend.Players.ToArray();
            // Replace immediately while the initial prefix may still be in the endpoint buffer.
            var replacement = session.ReadAsync(_ => Task.FromResult("新固定测试。"), settings);
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            await Pr9Tests.Until(() => sources.Count == 2 && session.Status.State == ReadingState.Playing);
            Assert.True(sources[0].Disposed); var accepted = oldBuffer.ProducedBytes;
            var positions = oldPlayers.Select(p => p.SourcePosition).ToArray(); sources[0].Release.TrySetResult();
            session.TogglePause(); Assert.Equal(ReadingState.Paused, session.Status.State);
            await Task.Delay(150); session.TogglePause(); Assert.Equal(ReadingState.Playing, session.Status.State);
            await Task.Delay(250);
            Assert.Equal(accepted, oldBuffer.ProducedBytes); Assert.Equal(positions, oldPlayers.Select(p => p.SourcePosition));
            Assert.All(backend.Players.Skip(oldPlayers.Length), p => Assert.True(p.SourcePosition > 0));
            session.Stop(); await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sources[1].Disposed); Assert.Equal(ReadingState.Idle, session.Status.State);
            log.WriteLine($"Actual WASAPI outputs={outputs.Length}: replacement during startup, pause/resume, Stop; old producer disposed and source positions stayed fixed. No headphone audibility assertion.");
        }
    }

    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task EndpointLoopbackChecksOnsetAcrossRepeatedStreamingAndCachedSessions()
    {
        using var enumerator = new MMDeviceEnumerator();
        var failures = new List<string>();
        var endpointFilter = Environment.GetEnvironmentVariable("SELEVOX_ONSET_ENDPOINT");
        var choices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput &&
            (string.IsNullOrEmpty(endpointFilter) || d.Label.Contains(endpointFilter, StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert.NotEmpty(choices);
        foreach (var choice in choices)
        {
            using var endpoint = enumerator.GetDevice(choice.Id);
            foreach (var lead in new[] { 0, 100, 150, 250 })
            for (var run = 0; run < 3; run++)
            foreach (var streaming in new[] { false, true })
            {
                var pcm = Marker(); using var buffer = new StreamingPcmBuffer();
                var clock = Stopwatch.StartNew();
                using var player = streaming
                    ? (IAudioPlayback)new StreamingWasapiPlayback(buffer.CreateReader(), .2f, enumerator.GetDevice(choice.Id), lead)
                    : new WasapiPlayback(Wave(pcm), .2f, enumerator.GetDevice(choice.Id), lead);
                var createMs = clock.Elapsed.TotalMilliseconds;
                if (streaming) { await buffer.WriteAsync(pcm, default); buffer.Complete(); }
                var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.Finished += error => done.TrySetResult(error);
                using var capture = new WasapiRecorderBuilder().WithDevice(endpoint).WithLoopbackCapture()
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
                var samples = new List<float>(); var gate = new object();
                long? firstQpc = null;
                capture.DataAvailable += (data, _, _, qpc) =>
                {
                    lock (gate)
                    {
                        firstQpc ??= qpc;
                        for (var i = 0; i + 8 <= data.Length; i += 8) samples.Add(BitConverter.ToSingle(data.Slice(i, 4)));
                    }
                };
                capture.StartRecording(); await Task.Delay(150);
                var playMs = clock.Elapsed.TotalMilliseconds;
                var playQpcMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency; player.Play();
                Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                var finishMs = clock.Elapsed.TotalMilliseconds;
                player.Dispose(); var disposeMs = clock.Elapsed.TotalMilliseconds;
                await Task.Delay(200); capture.StopRecording();
                float[] recorded; lock (gate) recorded = samples.ToArray();
                var (correlation, lag, onsetRatio) = Analyze(recorded);
                var firstSoundMs = firstQpc / 10000d + lag / 48d - playQpcMs;
                log.WriteLine($"endpoint={choice.Label}; run={run}; streaming={streaming}; lead={lead}ms; created={createMs:F1}ms; play={playMs:F1}ms; finished={finishMs:F1}ms; disposed={disposeMs:F1}ms; captured={recorded.Length}; correlation={correlation:F3}; aligned-onset={lag / 48d:F2}ms; play-to-marker={firstSoundMs:F2}ms; first-10ms/same-frequency-reference-rms={onsetRatio:F3}");
                if (correlation <= .8 || lead > 0 && onsetRatio <= .7)
                    failures.Add($"{choice.Label}, streaming={streaming}, lead={lead}, correlation={correlation:F3}, onsetRatio={onsetRatio:F3}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Locate using only 40..160ms, then inspect 0..10ms independently. Allows negative lags
    // so clipped captures cannot be made to pass by shifting alignment to the first sound.
    internal static (double Correlation, int Lag, double OnsetRatio) Analyze(float[] capture)
    {
        double best = 0; var lag = 0;
        for (var offset = -1920; offset < capture.Length - 7680; offset += 4)
        {
            double dot = 0, sourceEnergy = 0, recordedEnergy = 0;
            for (var i = 1920; i < 7680; i += 4)
            {
                var expected = Signal(i / 48000d); var actual = capture[offset + i];
                dot += expected * actual; sourceEnergy += expected * expected; recordedEnergy += actual * actual;
            }
            var correlation = dot / Math.Sqrt(sourceEnergy * recordedEnergy + 1e-30);
            if (correlation > best) { best = correlation; lag = offset; }
        }
        double Energy(int start, int end)
        {
            double energy = 0;
            for (var i = start; i < end; i++) if (lag + i >= 0 && lag + i < capture.Length) energy += capture[lag + i] * capture[lag + i];
            return energy / (end - start);
        }
        // Use 20..30ms of the SAME 997Hz marker: endpoint EQ can attenuate 997Hz
        // relative to the later frequencies without clipping the onset.
        return (best, lag, Math.Sqrt(Energy(0, 480) / (Energy(960, 1440) + 1e-30)));
    }
}
