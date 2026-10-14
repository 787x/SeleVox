using SeleVox.App;
using SeleVox.Cli;
using SeleVox.Core;
using NAudio.Wave;
using Xunit.Abstractions;
namespace SeleVox.Tests;
public sealed class AgentOnlineFact : FactAttribute
{ public AgentOnlineFact() { if (Environment.GetEnvironmentVariable("SELEVOX_AGENT_ONLINE_TESTS") != "1") Skip = "Requires explicit user online TTS authorization and SELEVOX_AGENT_ONLINE_TESTS=1."; } }
[Collection("WPF desktop")]
public sealed class AgentOnlineTests(ITestOutputHelper log)
{
    [AgentOnlineFact] public async Task RealMiMoThreeModes()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox");
        var path = Path.Combine(root, "agent.json"); var backup = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        var outputs = Path.GetFullPath(Path.Combine("artifacts", "pr14-online")); Directory.CreateDirectory(outputs);
        var library = new CloneLibrary(Path.Combine(root, "clone-samples")); CloneSample? added = null;
        using var cli = new AgentClient();
        async Task<string> Generate(string label, object parameters, string text)
        {
            var output = Path.Combine(outputs, label + ".wav");
            var reply = await cli.CallAsync("synthesize", new { text, parameters, output, overwrite = true, wait = true }); Assert.True(reply.Success, reply.Code);
            Assert.Equal("completed", reply.Data.GetProperty("State").GetString());
            using var wave = new WaveFileReader(output); Assert.True(wave.Length > 1000);
            log.WriteLine($"{label}: real service, complete {wave.WaveFormat}, duration={wave.TotalTime.TotalSeconds:0.00}s, frames={wave.Length / wave.WaveFormat.BlockAlign}."); return output;
        }
        try
        {
            // Replace test defaults temporarily, avoiding inherited incompatible per-provider advanced fields.
            new AgentStore(path).Update(c => c with { Defaults = AgentJson.Empty });
            Assert.True((await cli.CallAsync("config.set", new { allowOnline = true })).Success);
            if (AzureCredentials.MiMo().Read() is not null)
            {
                await Generate("mimo-preset", new { provider = "MiMo", mode = "Preset", voice = "冰糖", style = "自然平静", tags = Array.Empty<string>(), role = "播音员", direction = "温柔而清晰", autoSegment = true, segmentCharacters = 50 }, "你好，这是智能语音的短测试。[停顿]祝你今天一切顺利。为了验证长文本分段，我们继续朗读第二句话，检查生成文件是否完整，中文和表情符号也应正确处理。😀");
                var designed = await Generate("mimo-design", new { provider = "MiMo", mode = "VoiceDesign", voiceDescription = "年轻成年女性，普通话标准，声音清亮温暖，口齿清晰，语气自然平静。", role = "播音员", direction = "自然朗读", tags = Array.Empty<string>() }, "你好，这是声音设计的测试。窗外阳光明亮，微风轻轻吹过树梢。我们用平静自然的声音，讲述今天的小故事，也向每一位听众送上温暖的问候。希望你的一天愉快顺利。");
                added = await library.AddAsync("PR14 temporary verification", CloneSource.VoiceDesign, await CloneAudio.ReadAsync(designed, default), default);
                await Generate("mimo-clone", new { provider = "MiMo", mode = "VoiceClone", cloneSampleId = added.Id.ToString(), style = "平静自然", direction = "清晰朗读", tags = Array.Empty<string>() }, "你好，这是本地样本声音克隆测试。祝你今天愉快。");
            }
            else log.WriteLine("MiMo not tested: no saved credential.");

        }
        finally { if (added is not null) await library.DeleteAsync(added, default); if (backup is null) File.Delete(path); else await File.WriteAllBytesAsync(path, backup); }
    }
}
