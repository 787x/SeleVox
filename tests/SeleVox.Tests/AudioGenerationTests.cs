using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class AudioGenerationTests
{
    internal static byte[] Wave(bool floating = false, int frames = 2400)
    {
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), floating ? WaveFormat.CreateIeeeFloatWaveFormat(24000, 1) : new WaveFormat(24000, 16, 1)))
        {
            if (floating) { for (var i = 0; i < frames; ++i) writer.WriteSample(.25f); }
            else { var pcm = new byte[frames * 2]; for (var i = 0; i < frames; ++i) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)6000); writer.Write(pcm, 0, pcm.Length); }
        }
        return memory.ToArray();
    }
    internal sealed class Speech(Func<string, Settings, CancellationToken, Task<Stream>>? synth = null) : ISpeechSynthesis
    {
        internal List<string> Texts { get; } = [];
        internal List<Settings> Options { get; } = [];
        public Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation)
        { Texts.Add(text); Options.Add(settings); return synth?.Invoke(text, settings, cancellation) ?? Task.FromResult<Stream>(new MemoryStream(Wave(), false)); }
    }
    [Theory]
    [InlineData(TtsProvider.Windows)] [InlineData(TtsProvider.Azure)] [InlineData(TtsProvider.Edge)] [InlineData(TtsProvider.MiMo)] [InlineData(TtsProvider.OpenAiCompatible)]
    public async Task GenerationSnapshotAndRepeatedSaveOpenNeverSynthesizeAgain(TtsProvider provider)
    {
        var speech = new Speech(); var generator = new AudioGeneration(speech);
        var tags = new[] { "温柔" };
        var settings = new Settings(Provider: provider, MiMoOverallStyles: tags, OutputDeviceIds: ["no-device"], StartupSilenceMilliseconds: 250);
        var audio = await generator.GenerateAsync("原文🙂e\u0301", settings, CancellationToken.None);
        tags[0] = "清亮";
        Assert.Equal(["温柔"], audio.Configuration.MiMoOverallStyles!); Assert.Contains(provider.ToString(), audio.Description);
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < 3; ++i)
            {
                using var reader = new WaveFileReader(audio.Open()); Assert.Equal(2400, audio.SampleFrames); Assert.Equal(4800, reader.Length);
                var pcm = new byte[4800]; Assert.Equal(pcm.Length, reader.Read(pcm, 0, pcm.Length)); Assert.Equal(6000, BitConverter.ToInt16(pcm));
                var file = Path.Combine(folder, i + ".wav"); await audio.SaveAsync(file, false, CancellationToken.None);
                using var exported = HttpSpeech.ValidatedWave(await File.ReadAllBytesAsync(file)); Assert.Equal(4800, exported.Length);
            }
            Assert.Single(speech.Texts); Assert.Equal("原文🙂e\u0301", speech.Texts[0]);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Theory] [InlineData(TtsProvider.Windows)] [InlineData(TtsProvider.Azure)]
    public async Task LongUnicodeTextIsFullySynthesizedAndOnlySamplesAreMerged(TtsProvider provider)
    {
        var text = string.Concat(Enumerable.Repeat("中文🙂e\u0301。", 3000)); var speech = new Speech();
        var result = await new AudioGeneration(speech).GenerateAsync(text, new(Provider: provider), CancellationToken.None);
        Assert.Equal(text, string.Concat(speech.Texts)); Assert.True(speech.Texts.Count > 2);
        Assert.All(speech.Texts, p => { Assert.InRange(p.Length, 1, 800); Assert.False(char.IsLowSurrogate(p[0])); Assert.False(char.IsHighSurrogate(p[^1])); });
        using var reader = new WaveFileReader(result.Open()); Assert.Equal(2400L * speech.Texts.Count, result.SampleFrames);
        Assert.Equal(result.SampleFrames * 2, reader.Length);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PcmAndFloatRetainAllSamplesAndNoPlaybackPrefix(bool floating)
    {
        var wave = Wave(floating); var speech = new Speech((_, _, _) => Task.FromResult<Stream>(new MemoryStream(wave, false)));
        var result = await new AudioGeneration(speech).GenerateAsync("fixture", new(StartupSilenceMilliseconds: 250), CancellationToken.None);
        using var source = HttpSpeech.ValidatedWave(wave); using var target = new WaveFileReader(result.Open());
        Assert.Equal(source.WaveFormat, target.WaveFormat); Assert.Equal(source.Length, target.Length);
        var a = new byte[source.Length]; var b = new byte[target.Length]; source.ReadExactly(a); target.ReadExactly(b); Assert.Equal(a, b);
    }
    [Fact] public async Task FailureTruncationFormatChangesAndCancellationCannotProduceSuccess()
    {
        var truncated = Wave()[..^2];
        var speech = new Speech((_, _, _) => Task.FromResult<Stream>(new MemoryStream(truncated, false)));
        await Assert.ThrowsAsync<UserFeedbackException>(() => new AudioGeneration(speech).GenerateAsync("fixture", new(), CancellationToken.None));
        var count = 0; speech = new Speech((_, _, _) => Task.FromResult<Stream>(new MemoryStream(Wave(++count % 2 == 0), false)));
        await Assert.ThrowsAsync<UserFeedbackException>(() => new AudioGeneration(speech).GenerateAsync(new string('a', 1800), new(), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        speech = new Speech((_, _, _) => { cancellation.Cancel(); return Task.FromResult<Stream>(new MemoryStream(Wave(), false)); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AudioGeneration(speech).GenerateAsync("fixture", new(), cancellation.Token));
    }
    [Fact] public async Task SaveDoesNotOverwriteWithoutConfirmationAndCleansCancelledTemporary()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "audio.wav"); await File.WriteAllTextAsync(file, "existing");
            var audio = GeneratedAudio.Create(Wave(), new());
            await Assert.ThrowsAsync<IOException>(() => audio.SaveAsync(file, false, CancellationToken.None)); Assert.Equal("existing", await File.ReadAllTextAsync(file));
            Assert.Single(Directory.GetFiles(folder));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audio.SaveAsync(file, true, cancelled.Token));
            Assert.Single(Directory.GetFiles(folder)); Assert.Equal("existing", await File.ReadAllTextAsync(file));
            await audio.SaveAsync(file, true, CancellationToken.None); using var wave = HttpSpeech.ValidatedWave(await File.ReadAllBytesAsync(file)); Assert.Equal(4800, wave.Length);
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public void PreferencesExcludeTextAutomaticConsentAndDailyProviderAndOldSettingsRemainCompatible()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var main = new Settings(Provider: TtsProvider.MiMo, AutoSpeak: true, AutoAsk: true, AllowAutomaticMiMo: true, LlmSystemPrompt: "private fixture");
            var prefs = new WorkbenchPreferences(WorkbenchPreferences.OnlyTts(main), 100, 150, 780, 850); prefs.Save(path);
            var json = File.ReadAllText(path); Assert.DoesNotContain("private fixture", json);
            var restored = WorkbenchPreferences.Load(path, new()); Assert.Equal(TtsProvider.MiMo, restored.Tts.Provider); Assert.False(restored.Tts.AutoSpeak); Assert.False(restored.Tts.AutoAsk); Assert.False(restored.Tts.AllowAutomaticMiMo);
            Assert.Equal(150, restored.Top);
            File.WriteAllText(path, "{\"Rate\":1.2}"); var old = new SettingsStore(path).Load(); Assert.Null(old.Warning); Assert.Equal(1.2, old.Settings.Rate);
            Assert.True(main.AutoSpeak); Assert.True(main.AllowAutomaticMiMo);
        }
        finally { File.Delete(path); }
    }
    internal sealed class Player : IAudioPlayback
    {
        internal bool Disposed, Playing, Paused;
        public event Action<Exception?>? Finished;
        public void Play() { Playing = true; Paused = false; }
        public void Pause() { Playing = false; Paused = true; }
        public void Stop() => Playing = false;
        public void Dispose() => Disposed = true;
        internal void Finish(Exception? error = null) => Finished?.Invoke(error);
    }
    private sealed class Outputs : IAudioPlaybackFactory
    {
        internal readonly List<Player> Players = [];
        public IAudioPlayback CreatePlayback(Stream audio, Settings settings) { audio.Dispose(); var player = new Player(); Players.Add(player); return player; }
    }
    [Fact] public async Task CachedAudioReplacesReadingAndUsesUnifiedControlsWithZeroSynthesisAndCloseCancellation()
    {
        var speech = new Speech(); var outputs = new Outputs(); using var session = new ReadingSession(speech, outputs);
        await session.ReadAsync(_ => Task.FromResult("read"), new()); var old = outputs.Players[0];
        using var close = new CancellationTokenSource();
        var preview = session.PlayAudioAsync(() => new MemoryStream(Wave(), false), new(Provider: TtsProvider.MiMo), close.Token);
        await Wait(() => outputs.Players.Count == 2 && session.Status.State == ReadingState.Playing);
        Assert.True(old.Disposed); Assert.Single(speech.Texts);
        session.TogglePause(); Assert.Equal(ReadingState.Paused, session.Status.State); Assert.True(outputs.Players[1].Paused);
        session.TogglePause(); Assert.Equal(ReadingState.Playing, session.Status.State);
        close.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview); Assert.True(outputs.Players[1].Disposed); Assert.Equal(ReadingState.Idle, session.Status.State);
        preview = session.PlayAudioAsync(() => new MemoryStream(Wave(), false), new());
        await Wait(() => outputs.Players.Count == 3 && session.Status.State == ReadingState.Playing); outputs.Players[2].Finish(); await preview; Assert.True(outputs.Players[2].Disposed); Assert.Single(speech.Texts);
    }
    internal static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, timeout.Token); }
}
