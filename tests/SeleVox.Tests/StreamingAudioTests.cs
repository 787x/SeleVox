using System.Diagnostics;
using NAudio.CoreAudioApi;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

public sealed class StreamingAudioTests(ITestOutputHelper log)
{
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task LargeSseEventDrainsBothWasapiOutputsAndStopCancelsBackpressure()
    {
        var ids = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray(); Assert.Equal(2, ids.Length);
        var pcm = Tone(34000); // ~2.18 MiB SSE: exceeds the old 2 MiB event limit.
        using var input = new MiMoStreamingTests.DelayedStream(MiMoLargeEventTests.Event(pcm), "data: [DONE]\n\n");
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(input))));
        using var buffer = new StreamingPcmBuffer(); using var enumerator = new MMDeviceEnumerator();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var players = new List<StreamingWasapiPlayback>();
        using var group = AudioOutputs.CreateStreamingPlayback(buffer, new(Volume: .025f, OutputDeviceIds: ids), (id, reader, volume) =>
        { var player = new StreamingWasapiPlayback(reader, volume, enumerator.GetDevice(id)); players.Add(player); return player; });
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously); group.Finished += error => finished.TrySetResult(error);
        var metrics = new MiMoStreamingMetrics();
        var production = new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, cancellation.Token, metrics);
        try
        {
            await buffer.WaitForReadyAsync(cancellation.Token); Assert.False(production.IsCompleted); group.Play();
            await Task.Delay(250, cancellation.Token); Assert.All(players, p => Assert.True(p.SourcePosition > 0));
            group.Pause(); await Task.Delay(150, cancellation.Token); var positions = players.Select(p => p.SourcePosition).ToArray();
            await Task.Delay(250, cancellation.Token); Assert.Equal(positions, players.Select(p => p.SourcePosition).ToArray());
            group.Play(); input.Release.TrySetResult(); await production; buffer.Complete();
            Assert.Null(await finished.Task.WaitAsync(cancellation.Token)); Assert.All(players, p => Assert.Equal(pcm.Length, p.SourcePosition));
            Assert.True(metrics.MaxEventBytes > 2 * 1024 * 1024); Assert.InRange(buffer.PeakBufferedBytes, 1, buffer.Capacity);
            log.WriteLine($"Large SSE={metrics.MaxEventBytes} bytes; every real WASAPI output drained PCM={pcm.Length} bytes; pause/resume and bounded ring passed.");
        }
        finally { cancellation.Cancel(); group.Stop(); try { await production; } catch (OperationCanceledException) { } }

        using var stopInput = new MiMoStreamingTests.DelayedStream(MiMoLargeEventTests.Event(pcm), "data: [DONE]\n\n");
        using var stopHttp = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(stopInput))));
        var backend = new RealBackend(stopHttp); using var session = new ReadingSession(backend, backend);
        var pending = session.ReadAsync(_ => Task.FromResult("fixture"), new(Provider: TtsProvider.MiMo, Volume: .025f, OutputDeviceIds: ids));
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (session.Status.State != ReadingState.Playing) { Assert.NotEqual(ReadingState.Error, session.Status.State); await Task.Delay(10, stopTimeout.Token); }
        session.Stop(); await pending.WaitAsync(stopTimeout.Token); Assert.True(stopInput.Disposed); Assert.Equal(ReadingState.Idle, session.Status.State);
        var accepted = backend.Buffer!.ProducedBytes; stopInput.Release.TrySetResult(); await Task.Delay(100); Assert.Equal(accepted, backend.Buffer.ProducedBytes);
        log.WriteLine("Large-event Stop released both real outputs and cancelled backpressure; no late PCM append.");
    }
    internal static byte[] Tone(int milliseconds = 1000)
    {
        var bytes = new byte[48 * milliseconds];
        for (var i = 0; i < bytes.Length / 2; ++i) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), (short)(1500 * Math.Sin(2 * Math.PI * 440 * i / 24000)));
        return bytes;
    }
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task RealWasapiPlaysWhileSseStillOpenAndSurvivesUnderflowPauseAndMultipleOutputs()
    {
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).OrderBy(d => d.Label.Contains("Realtek") ? 0 : 1).Take(2).ToArray(); Assert.Equal(2, devices.Length);
        using var enumerator = new MMDeviceEnumerator();
        foreach (var ids in new[] { new[] { Settings.DefaultOutput }, new[] { devices[0].Id }, devices.Select(d => d.Id).ToArray() })
        {
            var clock = Stopwatch.StartNew();
            var server = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Tone()), MiMoStreamingTests.Event(Tone(500)) + "data: [DONE]\n\n");
            using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(server))));
            using var buffer = new StreamingPcmBuffer(); var players = new List<StreamingWasapiPlayback>();
            using var group = AudioOutputs.CreateStreamingPlayback(buffer, new(Volume: .1f, OutputDeviceIds: ids), (id, reader, volume) =>
            {
                var device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                var player = new StreamingWasapiPlayback(reader, volume, device); players.Add(player); return player;
            });
            var production = new MiMoSpeech(http, () => "fixture").SynthesizeStreamingAsync("fixture", new(), buffer, default);
            await buffer.WaitForReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); var firstBlock = clock.Elapsed;
            var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously); group.Finished += error => finished.TrySetResult(error);
            using var captureDevice = ids[0] == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(ids[0]);
            using var recorder = new NAudio.Wave.WasapiRecorderBuilder().WithDevice(captureDevice).WithLoopbackCapture()
                .WithFormat(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
            double peak = 0; var captureSync = new object();
            recorder.DataAvailable += (samples, _, _, _) =>
            { lock (captureSync) for (var sample = 0; sample + 4 <= samples.Length; sample += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(samples.Slice(sample, 4)))); };
            recorder.StartRecording();
            group.Play(); await Task.Delay(250); Assert.All(players, p => Assert.True(p.SourcePosition > 0)); var firstConsumption = clock.Elapsed;
            Assert.False(production.IsCompleted); Assert.False(server.Release.Task.IsCompleted);
            lock (captureSync) Assert.True(peak > .001, "Actual render endpoint loopback must contain non-silent audio before SSE completes.");
            group.Pause(); await Task.Delay(150); var paused = players.Select(p => p.SourcePosition).ToArray(); await Task.Delay(250); Assert.Equal(paused, players.Select(p => p.SourcePosition).ToArray());
            group.Play(); await Task.Delay(1200); Assert.False(finished.Task.IsCompleted); Assert.All(players, p => Assert.True(p.SilenceBytes > 0));
            server.Release.TrySetResult(); await production.WaitAsync(TimeSpan.FromSeconds(3)); var networkDone = clock.Elapsed; buffer.Complete();
            Assert.Null(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.All(players, p => Assert.Equal(Tone().Length + Tone(500).Length, p.SourcePosition));
            recorder.StopRecording();
            log.WriteLine($"Controlled SSE, outputs={ids.Length}: first PCM <= {firstBlock.TotalMilliseconds:F0} ms; observed real WASAPI consumption <= {firstConsumption.TotalMilliseconds:F0} ms; network done {networkDone.TotalMilliseconds:F0} ms; PCM bytes per output=72000. Not an acoustic first-sound measurement.");
        }
        log.WriteLine("Actual endpoints: " + string.Join("; ", devices.Select(d => d.Label)));
    }
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task RealStreamingStopCancelsPendingSseAndReleasesAllPlayers()
    {
        var source = new MiMoStreamingTests.DelayedStream(MiMoStreamingTests.Event(Tone(1500)), "data: [DONE]\n\n");
        using var http = new HttpClient(new MiMoStreamingTests.Handler((_, _) => Task.FromResult(MiMoStreamingTests.Response(source))));
        var backend = new RealBackend(http); using var session = new ReadingSession(backend, backend);
        var ids = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).Select(d => d.Id).ToArray(); Assert.Equal(2, ids.Length);
        var reading = session.ReadAsync(_ => Task.FromResult("fixture"), new(Provider: TtsProvider.MiMo, OutputDeviceIds: ids, Volume: .1f));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (session.Status.State != ReadingState.Playing) await Task.Delay(10, timeout.Token);
        await Task.Delay(250); Assert.False(reading.IsCompleted); session.Stop(); await reading.WaitAsync(timeout.Token);
        Assert.True(source.Disposed); Assert.Equal(ReadingState.Idle, session.Status.State); Assert.Equal(72000, backend.Buffer!.ProducedBytes);
        log.WriteLine("Real streaming session Stop cancelled open SSE and disposed multi-device WASAPI playback.");
    }
    internal sealed class RealBackend(HttpClient? http = null) : ISpeechSynthesis, IAudioPlaybackFactory, IStreamingSpeechSynthesis, IStreamingPlaybackFactory
    {
        public StreamingPcmBuffer? Buffer;
        public List<StreamingWasapiPlayback> Players = [];
        public bool UsesStreaming(Settings settings) => settings.UsesMiMoStreaming;
        public Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation) => new MiMoSpeech(http, http is null ? null : () => "fixture").SynthesizeStreamingAsync(text, settings, buffer, cancellation);
        public IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings)
        {
            Buffer = buffer; using var enumerator = new MMDeviceEnumerator();
            return AudioOutputs.CreateStreamingPlayback(buffer, settings, (id, reader, volume) =>
            {
                var device = id == Settings.DefaultOutput ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
                var player = new StreamingWasapiPlayback(reader, volume, device); Players.Add(player); return player;
            });
        }
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation) => new MiMoSpeech(http, http is null ? null : () => "fixture").SynthesizeAsync(text, settings, cancellation);
        public IAudioPlayback CreatePlayback(Stream audio, Settings settings) => AudioOutputs.CreatePlayback(audio, settings);
    }
}
