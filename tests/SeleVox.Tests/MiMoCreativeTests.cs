using System.Net;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class MiMoCreativeTests
{
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => send(r, t); }
    internal static HttpResponseMessage Response(byte[] wave) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(wave) } } } } })) };
    internal static Settings Options(MiMoCreativeMode mode) => new Settings(Provider: TtsProvider.MiMo, MiMoModel: MiMoCreative.Model(mode), MiMoOverallStyles: ["唱歌"], MiMoStyle: "平静") {
        MiMoVoiceDescription = "年轻、温暖的声音", MiMoReferenceAudio = new CloneAudio(AudioGenerationTests.Wave(frames: 120000), ".wav").DataUri };
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ControlledHttpUsesCorrectRolesModelAndModeParameters(int value)
    {
        var mode = (MiMoCreativeMode)value; var s = Options(mode); int calls = 0;
        using var http = new HttpClient(new Handler(async (r, token) => {
            ++calls; using var j = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token)); var root = j.RootElement;
            Assert.Equal(MiMoCreative.Model(mode), root.GetProperty("model").GetString()); Assert.False(root.GetProperty("stream").GetBoolean());
            var m = root.GetProperty("messages"); Assert.Equal("user", m[0].GetProperty("role").GetString());
            Assert.Equal(mode == MiMoCreativeMode.VoiceDesign ? "年轻、温暖的声音\n平静" : "平静", m[0].GetProperty("content").GetString());
            Assert.Equal("assistant", m[1].GetProperty("role").GetString()); Assert.Equal(mode == MiMoCreativeMode.PresetVoice ? "(唱歌)正文" : "正文", m[1].GetProperty("content").GetString());
            var a = root.GetProperty("audio"); Assert.Equal("wav", a.GetProperty("format").GetString());
            if (mode == MiMoCreativeMode.VoiceDesign) Assert.False(a.TryGetProperty("voice", out _));
            else Assert.Equal(mode == MiMoCreativeMode.VoiceClone ? s.MiMoReferenceAudio : "冰糖", a.GetProperty("voice").GetString());
            Assert.False(a.TryGetProperty("optimize_text_preview", out _)); return Response(AudioGenerationTests.Wave()); }));
        using var audio = await new MiMoSpeech(http, () => "fixture").SynthesizeAsync("正文", s, default);
        using var wav = new WaveFileReader(audio); Assert.Equal(4800, wav.Length); Assert.Equal(1, calls);
    }
    [Theory] [InlineData(50)] [InlineData(100)] [InlineData(3000)]
    public async Task ConfigurableSplitsPreserveUnicodeAndSamplesInOrder(int limit)
    {
        var text = string.Concat(Enumerable.Repeat("句内[叹气]中英文 é 😀 test。\n", 180)); var parts = new List<string>();
        using var http = new HttpClient(new Handler(async (r, token) => {
            using var j = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token)); var part = j.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            Assert.True(part.Length <= limit); Assert.True(Encoding.UTF8.GetByteCount(part) <= 3000); parts.Add(part);
            return Response(AudioGenerationTests.Wave()); }));
        using var audio = await new MiMoSpeech(http, () => "fixture").SynthesizeAsync(text, new Settings() { MiMoSegmentCharacters = limit }, default);
        using var wav = new WaveFileReader(audio); Assert.Equal(text, string.Concat(parts)); Assert.Equal(parts.Count * 4800, wav.Length);
        Assert.Equal(MiMoSpeech.SplitCreativeText(text, new() { MiMoSegmentCharacters = limit }), parts);
    }
    [Fact] public void NaturalBoundaryAndDisabledSegmentationAreSafe()
    {
        var s = new Settings() { MiMoSegmentCharacters = 50 }; var text = new string('a', 40) + "。" + new string('b', 35);
        Assert.Equal([text[..41], text[41..]], MiMoSpeech.SplitCreativeText(text, s));
        Assert.Throws<UserFeedbackException>(() => MiMoSpeech.SplitCreativeText(text, s with { MiMoAutoSegment = false }));
        Assert.Single(MiMoSpeech.SplitCreativeText("short", s with { MiMoAutoSegment = false }));
        Assert.Throws<UserFeedbackException>(() => MiMoSpeech.SplitCreativeText(new string('中', 1100), s with { MiMoSegmentCharacters = 3000, MiMoAutoSegment = false }));
    }
    [Theory] [InlineData(0)] [InlineData(49)] [InlineData(3001)]
    public void InvalidLengthsRejected(int limit) => Assert.Throws<UserFeedbackException>(() => MiMoSpeech.SplitCreativeText("text", new() { MiMoSegmentCharacters = limit }));
    [Theory] [InlineData("")] [InlineData("\u0000text")] [InlineData("   ")]
    public void InvalidTextRejected(string text) => Assert.Throws<UserFeedbackException>(() => MiMoSpeech.SplitCreativeText(text, new()));
    [Fact] public void EmptyDesignCloneAndOversizeInstructionsNeverSend()
    {
        Assert.Throws<UserFeedbackException>(() => MiMoSpeech.GetStyle(Options(MiMoCreativeMode.VoiceDesign) with { MiMoVoiceDescription = "" }));
        Assert.Throws<UserFeedbackException>(() => MiMoSpeech.CreateCreativeRequest("text", Options(MiMoCreativeMode.VoiceClone) with { MiMoReferenceAudio = null }, ""));
        Assert.Throws<UserFeedbackException>(() => MiMoSpeech.GetStyle(Options(MiMoCreativeMode.VoiceDesign) with { MiMoVoiceDescription = new string('中', 1000), MiMoStyle = "more" }));
        Assert.Throws<UserFeedbackException>(() => MiMoSpeech.SplitCreativeText(new string('x', 32769), new()));
    }
    [Fact] public void DirectorJoinsOnlyProvidedFieldsAndSnapshotDoesNotContainReference()
    {
        Assert.Equal("角色：主持人\n指导：慢一些", MiMoCreative.Director(" 主持人 ", "", " 慢一些 "));
        var s = Options(MiMoCreativeMode.VoiceClone);
        Assert.DoesNotContain("base64", JsonSerializer.Serialize(s));
        var result = GeneratedAudio.Create(AudioGenerationTests.Wave(), s);
        Assert.Null(result.Configuration.MiMoReferenceAudio); Assert.DoesNotContain("base64", result.Description);
    }
    [Fact] public async Task CancellationBetweenSegmentsNeverProducesPartialResult()
    {
        using var cancel = new CancellationTokenSource(); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { if (++calls == 2) cancel.Cancel(); return Task.FromResult(Response(AudioGenerationTests.Wave())); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MiMoSpeech(http, () => "fixture").SynthesizeAsync(new string('a', 300), new() { MiMoSegmentCharacters = 50 }, cancel.Token));
        Assert.Equal(2, calls);
    }
    [Fact] public void WorkbenchDefaultsPersistCreativeSettingsButNeverReferenceAudio()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var s = Options(MiMoCreativeMode.VoiceDesign) with { MiMoAutoSegment = false, MiMoSegmentCharacters = 750 };
            new WorkbenchPreferences(s).Save(path); var restored = WorkbenchPreferences.Load(path, new()).Tts;
            Assert.Equal(s.MiMoVoiceDescription, restored.MiMoVoiceDescription); Assert.False(restored.MiMoAutoSegment); Assert.Equal(750, restored.MiMoSegmentCharacters); Assert.Equal(s.MiMoModel, restored.MiMoModel);
            Assert.Null(restored.MiMoReferenceAudio); Assert.DoesNotContain("base64", File.ReadAllText(path));
            File.WriteAllText(path, "{\"Tts\":{},\"Width\":1100,\"Height\":850}"); var legacy = WorkbenchPreferences.Load(path, new()).Tts;
            Assert.True(legacy.MiMoAutoSegment); Assert.Equal(3000, legacy.MiMoSegmentCharacters);
        }
        finally { File.Delete(path); }
    }
}
