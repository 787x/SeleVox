using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

// Opt-in hardware tests: run with --filter Category=WindowsAudio on a desktop with
// an installed WinRT voice and a default render device. They emit fixture speech.
public sealed class WindowsAudioTests
{
    [WindowsAudioFact, Trait("Category", "WindowsAudio")]
    public async Task LocalVoiceProducesDecodableWaveAndWasapiPauseKeepsPosition()
    {
        Assert.NotEmpty(SpeechAudio.GetVoices());
        var speech = new SpeechAudio();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = string.Concat(Enumerable.Repeat("这是本地音频测试，检查暂停继续和停止。", 15));
        var audio = await speech.SynthesizeAsync(text, new(), timeout.Token);
        using var playback = (WasapiPlayback)speech.CreatePlayback(audio, 0.15f);
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
        using var slow = await speech.SynthesizeAsync("这是用于比较语速的固定测试句子。", new(voice.Id, 0.5), timeout.Token);
        using var fast = await speech.SynthesizeAsync("这是用于比较语速的固定测试句子。", new(voice.Id, 2), timeout.Token);
        using var slowReader = new WaveFileReader(slow);
        using var fastReader = new WaveFileReader(fast);
        Assert.Equal(WaveFormatEncoding.Pcm, slowReader.WaveFormat.Encoding);
        Assert.True(slowReader.TotalTime > fastReader.TotalTime);
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
