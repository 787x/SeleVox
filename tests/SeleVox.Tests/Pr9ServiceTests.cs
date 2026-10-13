using System.Diagnostics;
using SeleVox.App;
using SeleVox.Core;
using Xunit.Abstractions;

namespace SeleVox.Tests;

public sealed class Pr9ServiceTests(ITestOutputHelper log)
{
    [LlmServiceFact, Trait("Category", "LlmService")]
    public async Task SavedLlmAndMiMoCompareCompleteAndIncrementalWithoutLoggingContent()
        => await CompareAsync("请用两句简短的话解释水为什么会结冰，每句以句号结尾。", "short", TimeSpan.FromMinutes(4));

    [LlmServiceFact, Trait("Category", "LlmService")]
    public Task LongComplexQuestionComparesActualFirstAudioAndContinuity() => CompareAsync(
        "请分析下面这个工程问题并给出完整方案：一个普通权限运行的 Windows 桌面助手，支持选中文字后自动向 LLM 提问，将流式回答逐句送到在线 TTS，并同时输出到耳机和虚拟声卡。用户可能暂停一分钟、停止、连续提新问题、更换 LLM 服务地址或撤销自动上传授权；网络可能在首句播放后断开，一个输出设备也可能失效。请解释捕获与动作解耦、权限检查顺序、单一有效会话和代次隔离、有界队列和背压、TTS 合成与实际播放完成的区别、暂停恢复、取消和迟到回调、双设备故障隔离、部分回答与语音失败的 UI 状态，以及如何设计可控 SSE 和真实音频验证。还要讨论逐句方式相对完整回答方式在首声延迟、段间停顿、费用及回答完整性上的取舍。要求回答约 800 至 1000 个中文字符，分为 10 至 14 个自然段，每段包含一至两句完整句子，使用中文句号结束。给出具体因果关系和一个事件时序例子，避免 Markdown 表格、代码块和列表。不要缩写成几句总结。",
        "long-complex", TimeSpan.FromMinutes(10));

    private async Task CompareAsync(string question, string scenario, TimeSpan timeoutBudget)
    {
        var loaded = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "settings.json")).Load();
        Assert.Null(loaded.Warning); var key = AzureCredentials.Llm(loaded.Settings.LlmBaseUrl).Read(); Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.False(string.IsNullOrWhiteSpace(AzureCredentials.MiMo().Read()));
        foreach (var mode in new[] { AnswerReadingStrategy.Complete, AnswerReadingStrategy.Incremental })
        {
            var clock = Stopwatch.StartNew(); long firstContent = 0, firstSentence = 0, generationDone = 0, audioConsumed = 0;
            var backend = new StreamingAudioTests.RealBackend(); using var reading = new ReadingSession(backend, backend);
            var chat = new ChatCompletionClient();
            var sentenceBuffer = new AnswerSentenceBuffer(); long firstReadableSentence = 0;
            var starts = new List<long>(); var ends = new System.Collections.Concurrent.ConcurrentQueue<long>();
            var observedPlayers = 0;
            Task Read(string text, Settings settings, CancellationToken token)
            { if (firstSentence == 0) firstSentence = clock.ElapsedMilliseconds; return reading.ReadSentenceAsync(text, settings, token); }
            using var qa = new QuestionSession(async (text, settings, credential, delta, token) =>
            {
                var result = await chat.GenerateAsync(text, settings, credential, d => {
                    if (firstContent == 0) firstContent = clock.ElapsedMilliseconds;
                    foreach (var part in sentenceBuffer.Append(d)) if (firstReadableSentence == 0) firstReadableSentence = clock.ElapsedMilliseconds;
                    delta(d);
                }, token);
                generationDone = clock.ElapsedMilliseconds; return result;
            }, (text, settings) => Read(text, settings, default), reading.Stop, Read);
            var settings = loaded.Settings with { LlmStreaming = true, LlmAutoRead = true, LlmReadingStrategy = mode,
                Provider = TtsProvider.MiMo, MiMoStreaming = true, MiMoModel = "mimo-v2.5-tts", MiMoStyle = "", MiMoOverallStyles = [],
                Volume = .08f, OutputDeviceIds = [Settings.DefaultOutput] };
            var running = qa.AskAsync(question, settings, key!);
            using var timeout = new CancellationTokenSource(timeoutBudget);
            // Reading state is published after Play starts; inspect the actual PCM reader, not UI status.
            while (!running.IsCompleted)
            {
                if (reading.Status.State is ReadingState.Playing or ReadingState.Paused)
                    try {
                        var count = backend.Players.Count;
                        if (count > observedPlayers && backend.Players[count - 1].SourcePosition > 0)
                        {
                            observedPlayers = count; var start = clock.ElapsedMilliseconds; starts.Add(start);
                            if (audioConsumed == 0) audioConsumed = start;
                            backend.Players[count - 1].Finished += _ => ends.Enqueue(clock.ElapsedMilliseconds);
                            log.WriteLine($"progress scenario={scenario}; mode={mode}; audio-segment={count}; consumption={start}ms");
                        }
                    } catch (ObjectDisposedException) { }
                await Task.Delay(10, timeout.Token);
            }
            await running; Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.DoesNotContain("不完整", qa.Snapshot.Message);
            Assert.True(audioConsumed > 0, "No real PCM consumption observed.");
            var finished = ends.ToArray();
            var gaps = starts.Skip(1).Select((start, index) => index < finished.Length ? start - finished[index] : -1).ToArray();
            if (gaps.Length > 0) log.WriteLine($"segment-consumption-gaps-ms={string.Join(',', gaps)}");
            log.WriteLine($"paid-service scenario={scenario}; mode={mode}; request=0ms; first-content={firstContent}ms; first-readable-sentence={firstReadableSentence}ms; first-tts-submission={firstSentence}ms; first-audio-consumption={audioConsumed}ms; generation-done={generationDone}ms; audio-ended={clock.ElapsedMilliseconds}ms; tts-requests={backend.Players.Count}; answer-characters={qa.Snapshot.Answer.Length}");
        }
    }
}
