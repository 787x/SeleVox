using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class LlmServiceFactAttribute : FactAttribute
{
    public LlmServiceFactAttribute()
    { if (Environment.GetEnvironmentVariable("SELEVOX_LLM_SERVICE_TESTS") != "1") Skip = "Explicit opt-in required: uses saved LLM credentials and may incur a charge."; }
}
public sealed class LlmServiceTests
{
    [LlmServiceFact, Trait("Category", "LlmService")]
    public async Task RealSavedLlmStreamsFixedQuestionWithoutPersistingAnswer()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "settings.json");
        var loaded = new SettingsStore(path).Load(); Assert.Null(loaded.Warning); Assert.True(loaded.Settings.HasValidLlmConfiguration);
        var key = AzureCredentials.Llm(loaded.Settings.LlmBaseUrl).Read(); Assert.False(string.IsNullOrWhiteSpace(key));
        var chunks = 0; using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var answer = await new ChatCompletionClient().GenerateAsync("请用一句简短的话解释水为什么会结冰。", loaded.Settings with { LlmStreaming = true }, key!,
            text => { if (text.Length > 0) ++chunks; }, budget.Token);
        Assert.True(chunks > 0); Assert.True(!string.IsNullOrWhiteSpace(answer)); // Do not include provider content in diagnostics.
    }
}
