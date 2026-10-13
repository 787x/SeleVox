using System.Diagnostics;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

public sealed class Pr9ServiceTests(ITestOutputHelper log)
{
    [LlmServiceFact, Trait("Category", "LlmService")]
    public async Task SavedLlmAndMiMoCompareCompleteAndIncrementalWithoutLoggingContent()
    {
        var loaded = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "settings.json")).Load();
        Assert.Null(loaded.Warning); var key = AzureCredentials.Llm(loaded.Settings.LlmBaseUrl).Read(); Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.False(string.IsNullOrWhiteSpace(AzureCredentials.MiMo().Read()));
        foreach (var mode in new[] { AnswerReadingStrategy.Complete, AnswerReadingStrategy.Incremental })
        {
            var clock = Stopwatch.StartNew(); long firstContent = 0, firstSentence = 0, generationDone = 0, audioConsumed = 0;
            var backend = new StreamingAudioTests.RealBackend(); using var reading = new ReadingSession(backend, backend);
            var chat = new ChatCompletionClient();
            Task Read(string text, Settings settings, CancellationToken token)
            { if (firstSentence == 0) firstSentence = clock.ElapsedMilliseconds; return reading.ReadSentenceAsync(text, settings, token); }
            using var qa = new QuestionSession(async (text, settings, credential, delta, token) =>
            {
                var result = await chat.GenerateAsync(text, settings, credential, d => { if (firstContent == 0) firstContent = clock.ElapsedMilliseconds; delta(d); }, token);
                generationDone = clock.ElapsedMilliseconds; return result;
            }, (text, settings) => Read(text, settings, default), reading.Stop, Read);
            var settings = loaded.Settings with { LlmStreaming = true, LlmAutoRead = true, LlmReadingStrategy = mode,
                Provider = TtsProvider.MiMo, MiMoStreaming = true, MiMoModel = "mimo-v2.5-tts", MiMoStyle = "", MiMoOverallStyles = [],
                Volume = .08f, OutputDeviceIds = [Settings.DefaultOutput] };
            var running = qa.AskAsync("请用两句简短的话解释水为什么会结冰，每句以句号结尾。", settings, key!);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            // Reading state is published after Play starts; inspect the actual PCM reader, not UI status.
            while (!running.IsCompleted)
            {
                if (audioConsumed == 0 && reading.Status.State is ReadingState.Playing or ReadingState.Paused)
                    try { if (backend.Players.Any(p => p.SourcePosition > 0)) audioConsumed = clock.ElapsedMilliseconds; } catch (ObjectDisposedException) { }
                await Task.Delay(10, timeout.Token);
            }
            await running; Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.DoesNotContain("不完整", qa.Snapshot.Message);
            Assert.True(audioConsumed > 0, "No real PCM consumption observed.");
            log.WriteLine($"paid-service mode={mode}; request=0ms; first-content={firstContent}ms; first-sentence={firstSentence}ms; first-audio-consumption={audioConsumed}ms; generation-done={generationDone}ms; audio-ended={clock.ElapsedMilliseconds}ms; tts-requests={backend.Players.Count}");
        }
    }
}
