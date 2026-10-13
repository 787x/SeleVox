using NAudio.Wave;
using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

// Explicit opt-in only. Uses saved Windows credentials, sends fixed fixture text;
// never prints keys, text, response bodies or network exception detail.
public sealed class HttpSpeechServiceTests
{
    [HttpSpeechServiceFact, Trait("Category", "HttpSpeechService")]
    public async Task RealMiMoChineseAndEnglish()
    {
        foreach (var voice in new[] { "冰糖", "Mia" })
        {
            var settings = new Settings(Provider: TtsProvider.MiMo, MiMoVoice: voice,
                MiMoModel: Environment.GetEnvironmentVariable("SELEVOX_MIMO_MODEL") ?? "mimo-v2.5-tts");
            using var audio = await new MiMoSpeech().SynthesizeAsync(voice == "Mia" ? "This is a fixed speech test." : "这是固定语音测试。", settings, default);
            await ValidateAndOptionallyPlay(audio, settings);
        }
    }
    [HttpSpeechServiceFact, Trait("Category", "HttpSpeechService")]
    public async Task RealOpenAiChineseAndEnglish()
    {
        var settings = new Settings(Provider: TtsProvider.OpenAiCompatible,
            OpenAiBaseUrl: Environment.GetEnvironmentVariable("SELEVOX_OPENAI_BASE_URL") ?? "https://api.openai.com/v1",
            OpenAiModel: Environment.GetEnvironmentVariable("SELEVOX_OPENAI_MODEL") ?? "gpt-4o-mini-tts",
            OpenAiVoice: Environment.GetEnvironmentVariable("SELEVOX_OPENAI_VOICE") ?? "coral");
        foreach (var text in new[] { "这是固定语音测试。", "This is a fixed speech test." })
        {
            using var audio = await new OpenAiSpeech().SynthesizeAsync(text, settings, default);
            await ValidateAndOptionallyPlay(audio, settings);
        }
    }
    private static async Task ValidateAndOptionallyPlay(Stream audio, Settings settings)
    {
        var bytes = new byte[audio.Length]; await audio.ReadExactlyAsync(bytes);
        using var reader = HttpSpeech.ValidatedWave(bytes); Assert.True(reader.Length > 0);
        if (Environment.GetEnvironmentVariable("SELEVOX_AUDIO_TESTS") != "1") return;
        using var group = AudioOutputs.CreatePlayback(new MemoryStream(bytes, false), settings);
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        group.Finished += error => completed.TrySetResult(error); group.Play();
        Assert.Null(await completed.Task.WaitAsync(TimeSpan.FromSeconds(60)));
    }
}
public sealed class HttpSpeechServiceFactAttribute : FactAttribute
{
    public HttpSpeechServiceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SELEVOX_HTTP_TTS_TESTS") != "1")
            Skip = "Set SELEVOX_HTTP_TTS_TESTS=1 and save provider keys to explicitly enable billable service tests.";
    }
}
