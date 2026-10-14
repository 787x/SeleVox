using System.Text.Json;
using SeleVox.App;
using SeleVox.Core;
using SeleVox.Cli;
namespace SeleVox.Tests;
public sealed class AgentConfigurationTests
{
    private static JsonElement J(string value) => JsonDocument.Parse(value).RootElement.Clone();
    [Fact] public void ParametersInheritPerFieldAndExplicitEmptyClears()
    {
        var main = new Settings(Provider: TtsProvider.MiMo, MiMoStyle: "desktop", Rate: .9, OutputDeviceIds: ["desktop"], MiMoOverallStyles: ["温柔"]);
        var c = new AgentConfiguration { Defaults = J("""{"voice":"Mia","style":"agent","rate":1.1,"devices":["agent"]}"""), Presets = new() { ["p"] = J("""{"rate":1.3,"style":"preset"}""") } };
        var (s, _) = AgentParameters.Resolve(main, c, "p", J("""{"style":"","tags":[],"devices":[],"rate":1.5}"""));
        Assert.Equal("Mia", s.MiMoVoice); Assert.Equal(1.5, s.Rate); Assert.Empty(s.MiMoStyle); Assert.Empty(s.MiMoOverallStyles!); Assert.Empty(s.OutputDeviceIds!);
        Assert.Equal("desktop", main.MiMoStyle); Assert.Equal(1.1, c.Defaults.GetProperty("rate").GetDouble());
    }
    [Fact] public void ModelAndModeRespectTheSameLayerPriority()
    {
        var config = new AgentConfiguration { Defaults = J("""{"provider":"MiMo","mode":"VoiceDesign","voiceDescription":"女声"}""") };
        Assert.Equal("mimo-v2.5-tts", AgentParameters.Resolve(new(), config, null, J("""{"model":"mimo-v2.5-tts"}""")).Settings.MiMoModel);
        Assert.Equal("mimo-v2.5-tts-voiceclone", AgentParameters.Resolve(new(), config, null, J("""{"mode":"VoiceClone"}""")).Settings.MiMoModel);
        Assert.Throws<InvalidDataException>(() => AgentParameters.Resolve(new(), config, null, J("""{"mode":"VoiceDesign","model":"mimo-v2.5-tts"}""")));
    }
    [Fact] public void ProviderOverrideIgnoresInheritedInapplicableAdvancedFieldsAndEmptyLocalVoiceResets()
    {
        var defaults = new AgentConfiguration { Defaults = J("""{"provider":"MiMo","style":"温柔","mode":"VoiceDesign","voiceDescription":"女声"}""") };
        var s = AgentParameters.Resolve(new(VoiceId: "old"), defaults, null, J("""{"provider":"Windows","voice":""}""")).Settings;
        Assert.Equal(TtsProvider.Windows, s.Provider); Assert.Null(s.VoiceId);
    }
    [Fact] public void NullVoiceClearsAndServiceAddressesCannotBeOverridden()
    {
        var s = AgentParameters.Resolve(new(VoiceId: "desktop"), new(), null, J("""{"voice":null}""")).Settings;
        Assert.Null(s.VoiceId);
        foreach (var field in new[] { "apiKey", "OpenAiBaseUrl", "MiMoReferenceAudio", "text", "unknown" }) Assert.Throws<InvalidDataException>(() => AgentParameters.Validate(AgentJson.Element(new Dictionary<string, object> { [field] = "secret" })));
        Assert.Throws<InvalidDataException>(() => AgentParameters.Validate(J("""{"rate":1,"rate":2}""")));
    }
    [Theory] [InlineData("Preset", "mimo-v2.5-tts")] [InlineData("VoiceDesign", "mimo-v2.5-tts-voicedesign")] [InlineData("VoiceClone", "mimo-v2.5-tts-voiceclone")]
    public void CreativeModesUseExistingMiMoRules(string mode, string model)
    {
        var id = Guid.NewGuid().ToString();
        var fields = AgentJson.Element(new { provider = "MiMo", mode, role = "主持人", scene = "电台", direction = "平静", voiceDescription = "清亮的成年女声", cloneSampleId = id });
        var (s, clone) = AgentParameters.Resolve(new(), new(), null, fields);
        Assert.Equal(model, s.MiMoModel); Assert.Equal(id, clone); Assert.Contains("指导：平静", s.MiMoStyle);
        if (mode == "VoiceClone") s = s with { MiMoReferenceAudio = "data:audio/wav;base64,AA==" };
        var json = AgentJson.Element(MiMoSpeech.CreateCreativeRequest("你好😀[停顿]", s, MiMoSpeech.GetStyle(s)));
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal(mode != "VoiceDesign", json.GetProperty("audio").TryGetProperty("voice", out _));
        Assert.Equal("你好😀[停顿]", json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
    }
    [Theory] [InlineData("Windows", "pitch")] [InlineData("Edge", "mode")] [InlineData("MiMo", "instructions")]
    public void UnsupportedProviderFieldsAreRejected(string provider, string field)
    {
        var d = new Dictionary<string, object> { ["provider"] = provider, [field] = field == "pitch" ? 5 : "VoiceDesign" };
        Assert.Throws<InvalidDataException>(() => AgentParameters.Resolve(new(), new(), null, AgentJson.Element(d)));
    }
    [Fact] public void AtomicStoreRoundTripExclusiveLeaseAndCorruptFilePreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "agent.json"); var store = new AgentStore(path);
        try
        {
            store.Update(c => c with { Defaults = J("""{"provider":"MiMo","style":"","tags":[]}"""), Presets = new() { ["test"] = J("""{"voice":"Mia"}""") } });
            Assert.False(store.Load().ExposeSetDefaults); Assert.False(store.Load().ExposeDeletePreset);
            Assert.Equal("Mia", store.Load().Presets["test"].GetProperty("voice").GetString());
            using (var lease = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Throws<IOException>(() => store.Update(c => c));
            File.WriteAllText(path, "invalid"); Assert.ThrowsAny<JsonException>(() => store.Update(c => c)); Assert.Equal("invalid", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task CliConversionInputAndDuplicateFields()
    {
        Assert.False(CliArguments.WantsHelp(["speak", "--text", "--help"]));
        Assert.True(CliArguments.WantsHelp(["speak", "--text", "fixture", "--help"]));
        var args = await CliArguments.ParseAsync(["speak", "--text", "你好😀", "--provider", "MiMo", "--tags", "[]", "--style", "", "--wait"], default);
        Assert.Equal("speak", args.Command); Assert.Equal("你好😀", args.Arguments["text"]); Assert.Equal(true, args.Arguments["wait"]);
        Assert.Empty(((JsonElement)args.Arguments["parameters"]!).GetProperty("tags").EnumerateArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => CliArguments.ParseAsync(["speak", "--text", "x", "--rate", "1", "--parameters", "{\"rate\":2}"], default));
        var file = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(file, "UTF-8 中文😀"); var parsed = await CliArguments.ParseAsync(["synthesize", "--file", file, "--output", "out.wav"], default); Assert.Equal("UTF-8 中文😀", parsed.Arguments["text"]); }
        finally { File.Delete(file); }
    }
    [Fact] public async Task FullUnicodeTextAndSupportedInstructionLengthFitTheWire()
    {
        var instructions = new string('中', 4096); var parameters = AgentJson.Element(new { provider = "OpenAiCompatible", instructions });
        Assert.Equal(instructions, AgentParameters.Resolve(new(), new(), null, parameters).Settings.OpenAiInstructions);
        using var stream = new MemoryStream();
        await AgentWire.WriteAsync(stream, new AgentRequest(1, "synthesize", AgentJson.Element(new { text = new string('中', 32768), parameters, output = "out.wav" })), default);
        stream.Position = 0; var read = await AgentWire.ReadAsync<AgentRequest>(stream, default); Assert.Equal(32768, read.Arguments.GetProperty("text").GetString()!.Length);
    }
    [Fact] public async Task WireRejectsSizeAndUnknownFields()
    {
        using var bad = new MemoryStream(BitConverter.GetBytes(AgentWire.MaxMessageBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentWire.ReadAsync<AgentRequest>(bad, default));
        using var stream = new MemoryStream(); await AgentWire.WriteAsync(stream, new AgentRequest(1, "providers", AgentJson.Empty), default); stream.Position = 0;
        Assert.Equal("providers", (await AgentWire.ReadAsync<AgentRequest>(stream, default)).Command);
    }
    [Fact] public void ExportRejectsJunctionEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); var outside = root + "-outside";
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside); var link = Path.Combine(root, "link");
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                { ArgumentList = { "/c", "mklink", "/J", link, outside }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            Assert.True(process!.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
            Assert.Throws<UnauthorizedAccessException>(() => AgentExport.Resolve("link/escape.wav", root, true));
            Directory.Delete(link); Assert.False(File.Exists(Path.Combine(outside, "escape.wav")));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(root, true); Directory.Delete(outside, true); }
    }
    [Fact] public void ExportRejectsTraversalAlternateStreamsAndDirectoryLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => AgentExport.Resolve("../escape.wav", root, true));
            Assert.Throws<InvalidDataException>(() => AgentExport.Resolve("x.wav:stream.wav", root, true));
            using var protect = AgentExport.Protect(Path.Combine(root, "valid.wav"), root);
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
        }
        finally { Directory.Delete(root, true); }
    }
}
