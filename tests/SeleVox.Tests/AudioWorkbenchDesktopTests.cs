using System.Windows.Controls;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

[Collection("WPF desktop")]
public sealed class AudioWorkbenchDesktopTests(ITestOutputHelper log)
{
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public Task RealDefaultSampleCachedSingleDualPauseStopSaveAndClose() => OcrDesktopTests.RunSta(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var savedFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        var voice = SpeechAudio.GetVoices().First(v => v.Label.Contains("(zh-"));
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).Take(2).ToArray(); Assert.Equal(2, devices.Length);
        var main = new Settings(VoiceId: voice.Id, Volume: .1f, StartupSilenceMilliseconds: 250);
        var speech = new CountingSpeech(); using var reading = new ReadingSession(speech, new SpeechAudio());
        GeneratedAudio? result = null;
        var window = new AudioWorkbenchWindow(() => main, () => { }, (audio, token) => { result = audio; return reading.PlayAudioAsync(audio.Open, main, token); }, reading.TogglePause, reading.Stop, path, speech);
        try
        {
            window.Show(); await window.GenerateAsync(true); Assert.Equal(1, speech.Count); Assert.NotNull(result); Assert.Equal(ReadingState.Idle, reading.Status.State);
            Assert.True(result!.SampleFrames > 2400); using var source = new WaveFileReader(result.Open());
            await result.SaveAsync(savedFile, false, CancellationToken.None);
            using var saved = HttpSpeech.ValidatedWave(await File.ReadAllBytesAsync(savedFile)); Assert.Equal(source.Length, saved.Length); Assert.Equal(source.WaveFormat, saved.WaveFormat);
            Assert.Equal(result.SampleFrames, saved.Length / saved.WaveFormat.BlockAlign);
            log.WriteLine($"Local fixed sample: {voice.Label}; {saved.WaveFormat}; frames={result.SampleFrames}; original and export lengths match (playback prefix excluded).");
            ((TextBox)window.FindName("InputText")).Text = string.Concat(Enumerable.Repeat("这是工作台缓存试听测试，暂停和停止使用统一播放控制。", 12));
            await window.GenerateAsync(false); Assert.Equal(2, speech.Count); Assert.Equal(ReadingState.Idle, reading.Status.State);
            foreach (var ids in new[] { new[] { Settings.DefaultOutput }, new[] { devices[0].Id }, devices.Select(d => d.Id).ToArray() })
            {
                main = main with { OutputDeviceIds = ids };
                var preview = window.PreviewAsync(); await AudioGenerationTests.Wait(() => reading.Status.State == ReadingState.Playing);
                reading.TogglePause(); Assert.Equal(ReadingState.Paused, reading.Status.State); await Task.Delay(100);
                reading.TogglePause(); Assert.Equal(ReadingState.Playing, reading.Status.State); await Task.Delay(150);
                reading.Stop(); await preview; Assert.Equal(ReadingState.Idle, reading.Status.State); Assert.Equal(2, speech.Count);
                log.WriteLine($"Cached playback controls passed on {ids.Length} output(s).");
            }
            var closingPreview = window.PreviewAsync(); await AudioGenerationTests.Wait(() => reading.Status.State == ReadingState.Playing); window.Close(); await closingPreview;
            Assert.Equal(ReadingState.Idle, reading.Status.State); Assert.Equal(2, speech.Count);
            log.WriteLine("Closing the independent workbench cancelled its active cached playback.");
        }
        finally { window.Close(); File.Delete(path); File.Delete(savedFile); }
    });
    private sealed class CountingSpeech : ISpeechSynthesis
    {
        internal int Count;
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        { Interlocked.Increment(ref Count); return new SpeechAudio().SynthesizeAsync(text, settings, cancellation); }
    }
}
