using NAudio.CoreAudioApi;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

// Explicit opt-in: only fixed fixture text is sent to Microsoft. No Azure key.
public sealed class EdgeServiceTests(ITestOutputHelper log)
{
    [EdgeServiceFact, Trait("Category", "EdgeService")]
    public async Task RealVoiceListIncludesChineseAndEnglish()
    {
        var voices = await new EdgeSpeech().GetVoicesAsync(default);
        Assert.Contains(voices, v => v.Id == "zh-CN-XiaoxiaoNeural");
        Assert.Contains(voices, v => v.Locale == "en-US");
        log.WriteLine($"Service returned {voices.Count} voices; zh-CN and en-US available.");
    }

    [EdgeServiceFact, Trait("Category", "EdgeService")]
    public async Task TransportHandshakeDiagnosticUsesOnlySanitizedErrorCategories()
    {
        try
        {
            await foreach (var _ in new SeleVox.App.EdgeTransport.Communicate("固定连接测试。", "zh-CN-XiaoxiaoNeural").StreamAsync()) { }
        }
        catch (Exception error)
        {
            for (Exception? e = error; e is not null; e = e.InnerException)
            {
                log.WriteLine($"Type={e.GetType().Name}; HResult={e.HResult}");
                if (e is HttpRequestException http) log.WriteLine($"HTTP={http.StatusCode}; category={http.HttpRequestError}");
                if (e is System.Net.WebSockets.WebSocketException socket) log.WriteLine($"WebSocket category={socket.WebSocketErrorCode}");
                var status = System.Text.RegularExpressions.Regex.Match(e.Message, @"(?:status code|HTTP)[^0-9]*([0-9]{3})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (status.Success) log.WriteLine("Handshake status=" + status.Groups[1].Value);
            }
            throw EdgeSpeech.Feedback(error);
        }
    }

    [EdgeServiceFact, Trait("Category", "EdgeService")]
    public async Task RealChineseRateAndPitchDecode()
    {
        var speech = new EdgeSpeech();
        async Task<(TimeSpan Duration, byte[] Bytes)> Synthesize(string text, string voice, double rate = 1, int pitch = 0)
        {
            using var audio = await speech.SynthesizeAsync(text, new(Provider: TtsProvider.Edge, EdgeVoice: voice, Rate: rate, EdgePitchHz: pitch), default);
            using var reader = new WaveFileReader(audio); Assert.True(reader.Length > 0);
            var bytes = new byte[reader.Length]; Assert.Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length));
            log.WriteLine($"Decoded {voice}: rate={rate}, pitch={pitch}, duration={reader.TotalTime}, PCM bytes={bytes.Length}.");
            return (reader.TotalTime, bytes);
        }
        const string text = "这是在线语音测试，检查语速和音调变化。";
        var normal = await Synthesize(text, "zh-CN-XiaoxiaoNeural");
        var slow = await Synthesize(text, "zh-CN-XiaoxiaoNeural", 0.5);
        var fast = await Synthesize(text, "zh-CN-XiaoxiaoNeural", 2);
        var pitched = await Synthesize(text, "zh-CN-XiaoxiaoNeural", pitch: 50);
        Assert.True(slow.Duration > normal.Duration && normal.Duration > fast.Duration);
        Assert.False(normal.Bytes.SequenceEqual(pitched.Bytes));
        log.WriteLine($"Chinese rate durations: {slow.Duration}, {normal.Duration}, {fast.Duration}; pitch changes samples.");
    }

    [EdgeServiceFact, Trait("Category", "EdgeService")]
    public async Task RealEnglishDecodeAndOptionalPlayback()
    {
        var speech = new SpeechAudio(); var settings = new Settings(Provider: TtsProvider.Edge, EdgeVoice: "en-US-AriaNeural", Volume: 0.1f);
        var audio = await speech.SynthesizeAsync("This is a fixed English speech synthesis test.", settings, default);
        using var reader = new WaveFileReader(audio); Assert.True(reader.Length > 0); var duration = reader.TotalTime;
        if (Environment.GetEnvironmentVariable("SELEVOX_AUDIO_TESTS") == "1")
        {
            audio.Position = 0; using var player = speech.CreatePlayback(audio, settings); player.Play(); await Task.Delay(500); player.Stop();
            log.WriteLine($"English decoded and played through production default WASAPI factory: {duration}.");
        }
        else log.WriteLine($"English decoded, playback not enabled: {duration}.");
    }

    [EdgeServiceFact, Trait("Category", "EdgeService")]
    public async Task RealLongChineseTextRequiresMultipleCompleteTurns()
    {
        var text = string.Concat(Enumerable.Repeat("这是在线长文本分段测试。", 110));
        var turns = EdgeSpeech.SplitText(text).Count(); Assert.True(turns > 1);
        using var audio = await new EdgeSpeech().SynthesizeAsync(text, new(Provider: TtsProvider.Edge, EdgeVoice: "zh-CN-XiaoxiaoNeural", Rate: 2), default);
        using var reader = new WaveFileReader(audio); Assert.True(reader.TotalTime > TimeSpan.FromSeconds(60)); Assert.True(reader.Length > 0);
        log.WriteLine($"Real long Chinese synthesis: {text.Length} characters, {turns} complete turns, {reader.TotalTime}, {reader.Length} PCM bytes. No acoustic word-by-word assertion.");
    }

    [EdgeAudioFact, Trait("Category", "EdgeService")]
    public async Task RealEdgeAudioUsesSingleAndMultipleWasapiOutputsAndSessionCancellation()
    {
        var speech = new SpeechAudio();
        var settings = new Settings(Provider: TtsProvider.Edge, EdgeVoice: "zh-CN-XiaoxiaoNeural", Volume: 0.1f);
        var text = string.Concat(Enumerable.Repeat("检查在线语音的暂停继续和停止。", 10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var audio = await speech.SynthesizeAsync(text, settings, timeout.Token);
        using var copy = new MemoryStream(); await audio.CopyToAsync(copy, timeout.Token); var bytes = copy.ToArray();
        using (var single = new WasapiPlayback(new MemoryStream(bytes, false), 0.1f))
        {
            single.Play(); await Task.Delay(400); single.Pause(); await Task.Delay(150);
            var position = single.SourcePosition; await Task.Delay(250); Assert.Equal(position, single.SourcePosition);
            single.Play(); await Task.Delay(350); Assert.True(single.SourcePosition > position); single.Stop();
        }
        var devices = AudioOutputs.GetDevices().Where(d => d.Id != Settings.DefaultOutput).OrderBy(d => d.Label.Contains("Realtek") ? 0 : 1).Take(2).ToArray();
        Assert.Equal(2, devices.Length); using var enumerator = new MMDeviceEnumerator();
        var players = devices.Select(d => new WasapiPlayback(new MemoryStream(bytes, false), 0.1f, enumerator.GetDevice(d.Id))).ToArray();
        using (var group = new MultiOutputPlayback(players))
        {
            group.Play(); await Task.Delay(400); group.Pause(); await Task.Delay(150);
            var positions = players.Select(p => p.SourcePosition).ToArray(); await Task.Delay(250);
            Assert.Equal(positions, players.Select(p => p.SourcePosition).ToArray());
            group.Play(); await Task.Delay(350); Assert.All(players.Zip(positions), p => Assert.True(p.First.SourcePosition > p.Second)); group.Stop();
        }
        using var factory = speech.CreatePlayback(new MemoryStream(bytes, false), settings with { OutputDeviceIds = devices.Select(d => d.Id).ToArray() });
        factory.Play(); await Task.Delay(300); factory.Stop();
        using var session = new ReadingSession(speech, speech);
        var pending = session.ReadAsync(_ => Task.FromResult(text), settings); await Task.Delay(100); session.Stop(); await pending;
        Assert.Equal(ReadingState.Idle, session.Status.State);
        pending = session.ReadAsync(_ => Task.FromResult(text), settings); await Task.Delay(100);
        await session.ReadAsync(_ => Task.FromResult("新请求替换旧请求。"), settings); await pending;
        Assert.Equal(ReadingState.Playing, session.Status.State); session.Stop();
        pending = session.ReadAsync(_ => Task.FromResult(text), settings); await Task.Delay(100); session.Dispose(); await pending;
        log.WriteLine("Edge decoded audio used default, explicit and dual WASAPI endpoints: " + string.Join("; ", devices.Select(d => d.Label)) + "; pause position/resume, Stop/replacement/dispose tested. No acoustic assertion.");
    }
}

public sealed class EdgeServiceFactAttribute : FactAttribute
{
    public EdgeServiceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_EDGE_TESTS") != "1") Skip = "Set SELEVOX_EDGE_TESTS=1 to send fixed fixture text to Microsoft online TTS.";
    }
}
public sealed class EdgeAudioFactAttribute : FactAttribute
{
    public EdgeAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_EDGE_TESTS") != "1" || Environment.GetEnvironmentVariable("SELEVOX_AUDIO_TESTS") != "1")
            Skip = "Requires SELEVOX_EDGE_TESTS=1 and SELEVOX_AUDIO_TESTS=1; emits fixture speech.";
    }
}
