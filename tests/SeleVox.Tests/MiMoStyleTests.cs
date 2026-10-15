using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class MiMoStyleTests
{
    [Fact] public void CatalogAndSearchIncludeEveryRequestedPreset()
    {
        string[] groups = ["基础情绪", "复合情绪", "整体语调", "音色定位", "人设腔调", "方言", "角色扮演", "特殊风格"];
        string[][] expected = [
            ["开心", "悲伤", "愤怒", "恐惧", "惊讶", "兴奋", "委屈", "平静", "冷漠"],
            ["怅然", "欣慰", "无奈", "愧疚", "释然", "嫉妒", "厌倦", "忐忑", "动情"],
            ["温柔", "高冷", "活泼", "严肃", "慵懒", "俏皮", "深沉", "干练", "凌厉"],
            ["磁性", "醇厚", "清亮", "空灵", "稚嫩", "苍老", "甜美", "沙哑", "醇雅"],
            ["夹子音", "御姐音", "正太音", "大叔音", "台湾腔"], ["东北话", "四川话", "河南话", "粤语"],
            ["孙悟空", "林黛玉"], ["唱歌"]];
        Assert.Equal(groups, MiMoStylePresets.Categories.Select(c => c.Name));
        for (var i = 0; i < groups.Length; i++)
        {
            Assert.Equal(expected[i], MiMoStylePresets.Categories[i].Styles);
            Assert.Equal(expected[i], Assert.Single(MiMoStylePresets.Search(groups[i])).Styles);
            foreach (var name in expected[i])
                Assert.Contains(name, MiMoStylePresets.Search(name).SelectMany(c => c.Styles));
        }
        Assert.Equal(48, MiMoStylePresets.Search(" ").SelectMany(c => c.Styles).Count());
        Assert.Empty(MiMoStylePresets.Search("上海话"));
    }
    [Fact] public void SelectionOrderDuplicatesRemoveClearAndSinging()
    {
        var tags = new MiMoStyleTags();
        tags.Add("温柔"); tags.Add("清亮"); tags.Add(" 上海话 "); tags.Add("温柔");
        Assert.Equal("(温柔 清亮 上海话)", MiMoStyleTags.GetPrefix(tags.Selected));
        tags.Remove("清亮"); tags.Add("清亮"); Assert.Equal("(温柔 上海话 清亮)", MiMoStyleTags.GetPrefix(tags.Selected));
        tags.Add("唱歌"); Assert.Equal("(唱歌)", MiMoStyleTags.GetPrefix(tags.Selected));
        tags.Add("新闻播报"); tags.Add("说书腔"); Assert.Equal("(新闻播报 说书腔)", MiMoStyleTags.GetPrefix(tags.Selected));
        tags.Add("singing"); Assert.Single(tags.Selected); tags.Add("温柔"); Assert.Equal(["温柔"], tags.Selected);
        tags.Clear(); Assert.Equal("", MiMoStyleTags.GetPrefix(tags.Selected)); Assert.Equal("", MiMoStyleTags.GetPrefix(null));
    }
    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("(上海话)")] [InlineData("上海（话）")] [InlineData("[唱歌]")]
    [InlineData("温柔\n")] [InlineData("\t清亮")] [InlineData("上海\0话")] [InlineData("温柔\u2028清亮")]
    [InlineData("\u202e上海话")]
    public void InvalidInputIsRejected(string name)
    {
        Assert.False(MiMoStyleTags.IsValidName(name));
        var tags = new MiMoStyleTags(["温柔"]);
        Assert.Throws<UserFeedbackException>(() => tags.Add(name)); Assert.Equal(["温柔"], tags.Selected);
        Assert.False(new Settings(MiMoOverallStyles: [name]).IsValid);
        Assert.Throws<UserFeedbackException>(() => MiMoStyleTags.GetPrefix([name]));
    }
    [Fact] public void LengthCountAndBudgetLimitsAreExplicit()
    {
        Assert.False(MiMoStyleTags.IsValidName(new string((char)0xd800, 1)));
        Assert.Throws<UserFeedbackException>(() => new MiMoStyleTags().Add(new string('a', 65)));
        Assert.False(new Settings(MiMoOverallStyles: ["温柔", "温柔"]).IsValid);
        Assert.False(new Settings(MiMoOverallStyles: ["唱歌", "温柔"]).IsValid);
        var tags = new MiMoStyleTags(Enumerable.Range(0, 16).Select(i => "风格" + i));
        Assert.Throws<UserFeedbackException>(() => tags.Add("第十七项"));
        var large = Enumerable.Range(0, 16).Select(i => new string('中', 62) + i.ToString("D2")).ToArray();
        Assert.Contains("预算", Assert.Throws<UserFeedbackException>(() => MiMoSpeech.GetTextBudget(MiMoStyleTags.GetPrefix(large))).Message);
        Assert.Throws<UserFeedbackException>(() => HttpSpeech.SplitText("e" + new string('\u0301', 1600), 2990, true).ToArray());
    }
    [Fact] public void SettingsRoundTripAndOldConfigsPreserveFreeText()
    {
        var path = Path.Combine(Path.GetTempPath(), "SeleVox-style-" + Guid.NewGuid(), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new Settings(MiMoStyle: "(上海话)\n请自然朗读。", MiMoOverallStyles: ["温柔", "清亮", "上海话"]);
            store.Save(settings); var loaded = store.Load(); Assert.Null(loaded.Warning);
            Assert.Equal(settings.MiMoOverallStyles, loaded.Settings.MiMoOverallStyles); Assert.Equal(settings.MiMoStyle, loaded.Settings.MiMoStyle);
            File.WriteAllText(path, "{\"Provider\":3,\"MiMoStyle\":\"(上海话)\"}");
            loaded = store.Load(); Assert.Null(loaded.Warning); Assert.Null(loaded.Settings.MiMoOverallStyles); Assert.Equal("(上海话)", loaded.Settings.MiMoStyle);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BothProtocolsApplyPrefixToEveryUnicodeSegmentWithoutChangingUserOrVoice(bool streaming)
    {
        var original = string.Concat(Enumerable.Repeat("第一句😀 é 👨‍👩‍👧‍👦。(用户原有标签)\r\n", 160));
        const string prefix = "(温柔 清亮 上海话)", instruction = "(上海话)\n请以自然、轻快的语气朗读。";
        var received = new List<string>();
        var boundaries = StringInfo.ParseCombiningCharacters(original).Append(original.Length).ToHashSet(); var offset = 0;
        using var client = new HttpClient(new MiMoStreamingTests.Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = json.RootElement;
            var messages = root.GetProperty("messages"); Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("user", messages[0].GetProperty("role").GetString()); Assert.Equal(instruction, messages[0].GetProperty("content").GetString());
            Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
            Assert.Equal("冰糖", root.GetProperty("audio").GetProperty("voice").GetString());
            var text = messages[1].GetProperty("content").GetString()!;
            Assert.StartsWith(prefix, text); Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 3000);
            var part = text[prefix.Length..]; Assert.NotEmpty(part); received.Add(part); offset += part.Length; Assert.Contains(offset, boundaries);
            if (streaming) return MiMoStreamingTests.Response(new MemoryStream(Encoding.UTF8.GetBytes(MiMoStreamingTests.Event([1, 2]) + "data: [DONE]\n\n")));
            return WaveResponse();
        }));
        var settings = new Settings(MiMoStyle: instruction, MiMoOverallStyles: ["温柔", "清亮", "上海话"]);
        if (streaming)
        {
            using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
            await new MiMoSpeech(client, () => "fixture").SynthesizeStreamingAsync(original, settings, buffer, default);
        }
        else { using var audio = await new MiMoSpeech(client, () => "fixture").SynthesizeAsync(original, settings, default); }
        Assert.True(received.Count > 1); Assert.Equal(original, string.Concat(received));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidSavedTagsFailBeforeSending(bool streaming)
    {
        using var client = new HttpClient(new MiMoStreamingTests.Handler((_, _) => throw new Exception("must not send")));
        var settings = new Settings(MiMoOverallStyles: ["温柔", "唱歌"]);
        using var buffer = new StreamingPcmBuffer();
        await Assert.ThrowsAsync<UserFeedbackException>(() => streaming
            ? new MiMoSpeech(client, () => "fixture").SynthesizeStreamingAsync("固定文本", settings, buffer, default)
            : new MiMoSpeech(client, () => "fixture").SynthesizeAsync("固定文本", settings, default));
    }
    [Theory]
    [InlineData(false, "", "")] [InlineData(true, "", "")]
    [InlineData(false, "上海话", "(上海话)")] [InlineData(true, "上海话", "(上海话)")]
    [InlineData(false, "唱歌", "(唱歌)")] [InlineData(true, "唱歌", "(唱歌)")]
    public async Task EmptySingleAndSingingPrefixesKeepInstructionAndInlineSourceTags(bool streaming, string tag, string prefix)
    {
        const string original = "(原有标签)固定测试。";
        using var client = new HttpClient(new MiMoStreamingTests.Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal("(上海话)", messages[0].GetProperty("content").GetString());
            Assert.Equal(prefix + original, messages[1].GetProperty("content").GetString());
            return streaming ? MiMoStreamingTests.Response(new MemoryStream(Encoding.UTF8.GetBytes(MiMoStreamingTests.Event([1, 2]) + "data: [DONE]\n\n"))) : WaveResponse();
        }));
        var settings = new Settings(MiMoStyle: "(上海话)", MiMoOverallStyles: tag.Length == 0 ? [] : [tag]);
        if (streaming)
        {
            using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
            await new MiMoSpeech(client, () => "fixture").SynthesizeStreamingAsync(original, settings, buffer, default);
        }
        else { using var audio = await new MiMoSpeech(client, () => "fixture").SynthesizeAsync(original, settings, default); }
    }
    private static HttpResponseMessage WaveResponse()
    {
        using var bytes = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(bytes), new WaveFormat(24000, 16, 1))) writer.Write([1, 2], 0, 2);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(bytes.ToArray()) } } } } })) };
    }
}
