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

    [LlmServiceFact, Trait("Category", "LlmService")]
    public async Task ThreeAdditionalLongQuestionPairsAlternateOrder()
    {
        var questions = AdditionalLongQuestions;
        const string length = AdditionalAnswerLength;
        var failures = new List<string>();
        for (var pair = 0; pair < questions.Length; pair++)
        {
            var order = pair % 2 == 0
                ? new[] { AnswerReadingStrategy.Incremental, AnswerReadingStrategy.Complete }
                : new[] { AnswerReadingStrategy.Complete, AnswerReadingStrategy.Incremental };
            foreach (var mode in order)
            {
                try { await CompareAsync(questions[pair] + length, $"additional-pair-{pair + 1}", TimeSpan.FromMinutes(10), mode); }
                catch (Exception ex) { var failure = $"pair={pair + 1}; mode={mode}; failure-type={ex.GetType().Name}"; failures.Add(failure); WriteMetric(failure); }
            }
        }
        Assert.Empty(failures);
    }

    private static readonly string[] AdditionalLongQuestions = new[]
        {
            "设计一个离线优先的笔记同步系统：三个设备可以离线编辑同一笔记，重新联网后可能出现重名、删除与修改冲突、附件上传一半中断、重复请求、客户端版本不同和服务端部分失败。请结合一个具体并发修改时序，解释数据模型、版本向量或其他版本标记、合并规则、幂等性、事务边界、附件生命周期、迁移兼容、隐私保护、用户可理解的冲突恢复界面以及如何用故障注入验证一致性。比较至少两种冲突解决方案，说明什么时候不能自动合并，给出分阶段交付与监控方案，不假设网络可靠。",
            "设计一个实时桌面音频调度系统：上游逐句生成音频，下游需要同时输出到耳机和虚拟声卡，两个设备采样率不同且其中一个可能拔出。用户可能暂停一分钟、连续替换任务或停止，网络可能只产生部分音频后断开。请分析有界缓冲、背压、独立设备读游标、采样率转换、首声与连续性的取舍、有限预取、取消代次、资源所有权、费用约束和错误反馈。给出一个含暂停、设备失效、新任务及迟到回调的具体时序，解释每一步保留或释放哪些资源，并提出可控网络和真实设备的验证策略。",
            "设计一个仓库补货决策工具：仓库有三类商品，平均每日需求分别为 20、50、100 件，供应提前期分别为 2、5、7 天，需求会波动，部分商品只有 30 天保质期，每周有运输容量限制。请在不假设需求完全稳定的前提下，解释再订货点、安全库存、预测误差、缺货和过期成本、联合运输安排及异常订单处理。给出可以手工复核的数值示例，明确所有额外假设，再讨论活动需求突增、供应迟到和数据缺失如何调整策略。最后设计最小可用实现、指标和验证方案，避免把简化模型的结果当作保证。"
        };
    private const string AdditionalAnswerLength = "回答约 800 至 1000 个中文字符，分成 10 至 14 个自然段，每段一至两句完整句子，使用中文句号结束。保持因果分析与具体例子，不使用 Markdown 表格、代码块或项目列表，不要缩写成几句总结。";

    [LlmServiceFact, Trait("Category", "LlmService")]
    public async Task RepeatSecondAndThirdLongIncrementalExperiments()
    {
        var failures = new List<string>();
        var selectedPair = int.TryParse(Environment.GetEnvironmentVariable("SELEVOX_QA_REPEAT_PAIR"), out var requestedPair) ? requestedPair : 0;
        Assert.Contains(selectedPair, new[] { 0, 2, 3 });
        for (var pair = 1; pair <= 2; pair++)
        {
            if (selectedPair != 0 && selectedPair != pair + 1) continue;
            try { await CompareAsync(AdditionalLongQuestions[pair] + AdditionalAnswerLength, $"additional-pair-{pair + 1}-repeat", TimeSpan.FromMinutes(10), AnswerReadingStrategy.Incremental); }
            catch (Exception ex) { var failure = $"repeat-pair={pair + 1}; failure-type={ex.GetType().Name}"; failures.Add(failure); WriteMetric(failure); }
        }
        Assert.Empty(failures);
    }

    private void WriteMetric(string value)
    {
        log.WriteLine(value);
        var path = Environment.GetEnvironmentVariable("SELEVOX_QA_TIMING_LOG");
        if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, value + Environment.NewLine);
    }

    private async Task CompareAsync(string question, string scenario, TimeSpan timeoutBudget, params AnswerReadingStrategy[] modes)
    {
        var loaded = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "settings.json")).Load();
        Assert.Null(loaded.Warning); var key = AzureCredentials.Llm(loaded.Settings.LlmBaseUrl).Read(); Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.False(string.IsNullOrWhiteSpace(AzureCredentials.MiMo().Read()));
        foreach (var mode in modes.Length > 0 ? modes : new[] { AnswerReadingStrategy.Complete, AnswerReadingStrategy.Incremental })
        {
            WriteMetric($"start scenario={scenario}; mode={mode}; model={loaded.Settings.LlmModel}; tts=mimo-v2.5-tts");
            var clock = Stopwatch.StartNew(); long firstContent = 0, firstSentence = 0, generationDone = 0, audioConsumed = 0;
            var backend = new StreamingAudioTests.RealBackend(); using var reading = new ReadingSession(backend, backend);
            var llmBudgetSeconds = int.TryParse(Environment.GetEnvironmentVariable("SELEVOX_QA_LLM_TOTAL_TIMEOUT_SECONDS"), out var requestedBudget)
                && requestedBudget is >= 180 and <= 600 ? requestedBudget : 180;
            var chat = new ChatCompletionClient(totalTimeout: TimeSpan.FromSeconds(llmBudgetSeconds));
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
                            WriteMetric($"progress scenario={scenario}; mode={mode}; audio-segment={count}; consumption={start}ms");
                        }
                    } catch (ObjectDisposedException) { }
                await Task.Delay(10, timeout.Token);
            }
            await running;
            var finished = ends.ToArray();
            var gaps = starts.Skip(1).Select((start, index) => index < finished.Length ? start - finished[index] : -1).ToArray();
            if (gaps.Length > 0) WriteMetric($"segment-consumption-gaps-ms={string.Join(',', gaps)}");
            WriteMetric($"paid-service scenario={scenario}; mode={mode}; request=0ms; first-content={firstContent}ms; first-readable-sentence={firstReadableSentence}ms; first-tts-submission={firstSentence}ms; first-audio-consumption={audioConsumed}ms; generation-done={generationDone}ms; audio-ended={clock.ElapsedMilliseconds}ms; tts-requests={backend.Players.Count}; answer-characters={qa.Snapshot.Answer.Length}; state={qa.Snapshot.State}; llm-total-budget-seconds={llmBudgetSeconds}; timeout-feedback={qa.Snapshot.Message.Contains("超时")}");
            Assert.Equal(QuestionState.Completed, qa.Snapshot.State); Assert.DoesNotContain("不完整", qa.Snapshot.Message);
            Assert.True(audioConsumed > 0, "No real PCM consumption observed.");
        }
    }
}
