using NAudio.CoreAudioApi;
using NAudio.Wave;
using SeleVox.Core;

namespace SeleVox.App;

// Explicit, short recording of the selected virtual capture only. Nothing is saved or logged.
internal static class VirtualMicrophoneTest
{
    internal static MemoryStream Tone()
    {
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(24000, 16, 1)))
        {
            for (var i = 0; i < 24000 * 3; i++) writer.WriteSample((float)Signal(i / 24000d));
        }
        return new(memory.ToArray(), false);
    }
    private static double Signal(double t)
    {
        var section = (int)(t / .5);
        if (section >= 6) return 0;
        var local = t - section * .5;
        if (local >= .4) return 0;
        var frequency = new[] { 600, 900, 1200, 900, 600, 1200 }[section];
        return .22 * Math.Min(1, Math.Min(local / .01, (.4 - local) / .01)) * Math.Sin(2 * Math.PI * frequency * t);
    }
    internal static double Correlation(float[] samples, int rate)
    {
        const int analysisRate = 6000;
        var signal = Enumerable.Range(0, analysisRate * 3).Select(i => Signal(i / (double)analysisRate)).ToArray();
        var downsample = Enumerable.Range(0, (int)((long)samples.Length * analysisRate / rate))
            .Select(i => (double)samples[(int)((long)i * rate / analysisRate)]).ToArray();
        var energy = signal.Sum(x => x * x); double best = 0;
        // Search latency, including the explicit 300 ms recording lead-in.
        for (var lag = 0; lag + signal.Length <= downsample.Length && lag < analysisRate * 2; lag++)
        {
            double cross = 0, received = 0;
            for (var i = 0; i < signal.Length; i++) { var value = downsample[i + lag]; cross += value * signal[i]; received += value * value; }
            if (received > .01) best = Math.Max(best, Math.Abs(cross) / Math.Sqrt(received * energy));
        }
        return best;
    }
    public static async Task<double> RunAsync(VirtualMicrophoneRoute route, CancellationToken cancellation)
    {
        if (!VirtualMicrophones.IsPair(route, VirtualMicrophones.Scan(), true))
            throw new UserFeedbackException("所选虚拟 Render / Capture 不可用或不属于同一受支持驱动。");
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(route.CaptureId);
        using var recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().Build();
        using var captured = new MemoryStream();
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        recorder.DataAvailable += (data, _, _, _) => { lock (gate) { if (captured.Length + data.Length <= 16 * 1024 * 1024) captured.Write(data); } };
        recorder.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
        using var playback = AudioOutputs.CreatePlayback(Tone(), new(OutputDeviceIds: [route.RenderId], Volume: 1, StartupSilenceMilliseconds: 0) { StrictOutputDevices = true });
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Finished += e => finished.TrySetResult(e);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(12));
        recorder.StartRecording();
        try
        {
            await Task.Delay(300, budget.Token).ConfigureAwait(false);
            playback.Play();
            if (await finished.Task.WaitAsync(budget.Token).ConfigureAwait(false) is not null) throw new UserFeedbackException("虚拟设备测试播放失败。");
            await Task.Delay(500, budget.Token).ConfigureAwait(false);
        }
        finally
        {
            try { playback.Stop(); }
            finally { recorder.StopRecording(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        }
        cancellation.ThrowIfCancellationRequested();
        if (await stopped.Task.ConfigureAwait(false) is not null) throw new UserFeedbackException("虚拟录音端点录音失败。");
        captured.Position = 0;
        using var raw = new RawSourceWaveStream(captured, recorder.WaveFormat);
        var provider = raw.ToSampleProvider();
        var values = new float[(int)(captured.Length / (recorder.WaveFormat.BitsPerSample / 8))];
        var count = provider.Read(values);
        var mono = new float[count / recorder.WaveFormat.Channels];
        for (var i = 0; i < mono.Length; i++) mono[i] = values[i * recorder.WaveFormat.Channels];
        var score = Correlation(mono, recorder.WaveFormat.SampleRate);
        if (score < .6) throw new UserFeedbackException("录音未确认与固定测试音频关联。请检查路由、其他程序占用及录音权限，勿启用未确认的配对。");
        return score;
    }
}
