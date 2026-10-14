using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeleVox.Core;

namespace SeleVox.App;

internal enum DiagnosticService { Windows, Azure, Edge, MiMo, OpenAiTts, Llm }
internal enum DiagnosticState { Untested, Running, Succeeded, Failed, Canceled, Expired }
internal sealed record DiagnosticResult(DiagnosticState State, string Message, TimeSpan Elapsed, DateTimeOffset TestedAt,
    ServiceFailure? Failure = null, int? HttpStatus = null);
internal sealed record DiagnosticSnapshot(DiagnosticService Service, Settings Settings, string? Key, string Identity)
{
    internal static DiagnosticSnapshot Create(DiagnosticService service, Settings settings, string? key)
    {
        settings = settings with { MiMoOverallStyles = settings.MiMoOverallStyles?.ToArray(),
            MiMoReferenceAudio = null, Applications = settings.Applications?.ToArray(),
            OutputDeviceIds = settings.OutputDeviceIds?.ToArray(), CustomPrompts = settings.CustomPrompts?.ToArray() };
        // In-memory only. Neither credentials nor fingerprints are persisted or displayed.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings) + "\n" + key)));
        return new(service, settings, key, identity);
    }
    internal Settings RequestSettings => Service == DiagnosticService.Llm
        ? Settings with { LlmSystemPrompt = ProviderDiagnostics.SystemPrompt }
        : Settings with { Provider = Service switch { DiagnosticService.Windows => TtsProvider.Windows,
            DiagnosticService.Azure => TtsProvider.Azure, DiagnosticService.Edge => TtsProvider.Edge,
            DiagnosticService.MiMo => TtsProvider.MiMo, _ => TtsProvider.OpenAiCompatible },
            MiMoReferenceAudio = null,
            MiMoVoiceDescription = Settings.MiMoModel == "mimo-v2.5-tts-voicedesign" ? "清晰自然的成年女性声音，语气平静。" : Settings.MiMoVoiceDescription };
    internal string Summary => Service switch {
        DiagnosticService.Windows => $"Windows · Voice={Safe(Settings.VoiceId ?? "系统默认")} · Rate={Settings.Rate:0.##}",
        DiagnosticService.Azure => $"Azure · Region={Safe(Settings.AzureRegion)} · Voice={Safe(Settings.AzureVoice)}",
        DiagnosticService.Edge => $"Edge（实验性在线服务）· Voice={Safe(Settings.EdgeVoice)} · Pitch={Settings.EdgePitchHz}",
        DiagnosticService.MiMo => $"MiMo · Model={Safe(Settings.MiMoModel)} · Voice={Safe(Settings.MiMoVoice)} · {(RequestSettings.UsesMiMoStreaming ? "SSE PCM 流式" : "完整 WAVE 非流式")}" +
            (Settings.MiMoModel == "mimo-v2.5-tts-voicedesign" ? " · 固定诊断音色描述" : ""),
        DiagnosticService.OpenAiTts => $"{SafeAddress(Settings.OpenAiBaseUrl)} · Model={Safe(Settings.OpenAiModel)} · Voice={Safe(Settings.OpenAiVoice)} · Speed={Settings.Rate:0.##}",
        _ => $"{SafeAddress(Settings.LlmBaseUrl)} · Model={Safe(Settings.LlmModel)} · {(Settings.LlmStreaming ? "SSE 流式" : "JSON 非流式")} · Thinking={Settings.LlmThinkingProtocol}/{Settings.LlmThinking} · 固定 System Prompt"
    };
    private static string Safe(string? text) => new((text ?? "未配置").Where(c => !char.IsControl(c)).Take(200).ToArray());
    // URL paths may contain secrets in compatible gateways. Display origin only.
    private static string SafeAddress(string address) => Settings.NormalizeServiceUrl(address) is { } url
        ? new Uri(url).GetLeftPart(UriPartial.Authority) + "（已保存地址，路径隐藏）" : "地址无效";
}

// A single diagnostic request at a time, no queued billable requests. No reading session or playback factory.
internal sealed class ProviderDiagnostics
{
    internal const string TestText = "Hello. 你好，语音检测。";
    internal const string Question = "请只回答：OK";
    internal const string SystemPrompt = "这是服务可用性检测。请用一个简短的正常回答响应用户。";
    internal static readonly TimeSpan TimeoutBudget = TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DiagnosticSnapshot, string, CancellationToken, Task<Stream>> synthesize;
    private readonly Func<DiagnosticSnapshot, StreamingPcmBuffer, CancellationToken, Task> stream;
    private readonly Func<DiagnosticSnapshot, CancellationToken, Task<string>> answer;
    private readonly Func<IReadOnlyList<VoiceChoice>> voices;
    private readonly TimeSpan timeout;
    internal ProviderDiagnostics(
        Func<DiagnosticSnapshot, string, CancellationToken, Task<Stream>>? synthesize = null,
        Func<DiagnosticSnapshot, StreamingPcmBuffer, CancellationToken, Task>? stream = null,
        Func<DiagnosticSnapshot, CancellationToken, Task<string>>? answer = null,
        Func<IReadOnlyList<VoiceChoice>>? voices = null, TimeSpan? timeout = null)
    {
        this.synthesize = synthesize ?? Synthesize;
        this.stream = stream ?? ((s, b, t) => new MiMoSpeech(readKey: () => s.Key).SynthesizeStreamingAsync(TestText, s.RequestSettings, b, t));
        this.answer = answer ?? ((s, t) => new ChatCompletionClient(requireConfiguredMode: true).GenerateAsync(Question, s.RequestSettings, s.Key!, _ => { }, t));
        this.voices = voices ?? SpeechAudio.GetVoices; this.timeout = timeout ?? TimeoutBudget;
    }
    internal static string? ReadKey(DiagnosticService service, Settings settings) => service switch {
        DiagnosticService.Azure => new AzureCredentials().Read(), DiagnosticService.MiMo => AzureCredentials.MiMo().Read(),
        DiagnosticService.OpenAiTts => AzureCredentials.OpenAi(settings.OpenAiBaseUrl).Read(),
        DiagnosticService.Llm => AzureCredentials.Llm(settings.LlmBaseUrl).Read(), _ => null
    };
    internal void Precheck(DiagnosticSnapshot snapshot)
    {
        try { PrecheckCore(snapshot); }
        catch (UserFeedbackException ex) when (ex.Failure == ServiceFailure.InvalidResponse)
        { throw new UserFeedbackException(ex.Message, ServiceFailure.Configuration); }
    }
    private void PrecheckCore(DiagnosticSnapshot snapshot)
    {
        var s = snapshot.RequestSettings;
        if (!s.IsValid) throw new UserFeedbackException("配置无效，请先保存合法设置。", ServiceFailure.Configuration);
        if (snapshot.Service is not (DiagnosticService.Windows or DiagnosticService.Edge) &&
            (string.IsNullOrWhiteSpace(snapshot.Key) || snapshot.Key.Length > 1280 || snapshot.Key.Any(char.IsControl)))
            throw new UserFeedbackException("当前服务地址的 Key 缺失或无效，请先保存凭据。", ServiceFailure.MissingCredential);
        switch (snapshot.Service)
        {
            case DiagnosticService.Windows:
                var available = voices();
                if (available.Count == 0 || s.VoiceId is not null && !available.Any(v => v.Id == s.VoiceId))
                    throw new UserFeedbackException("没有可用的已保存 Windows 本地语音，请安装或重新选择语音。", ServiceFailure.Configuration);
                break;
            case DiagnosticService.Azure:
                if (!Settings.IsRegionValid(s.AzureRegion) || string.IsNullOrWhiteSpace(s.AzureVoice))
                    throw new UserFeedbackException("Azure Region 或 Voice 未配置。", ServiceFailure.Configuration);
                break;
            case DiagnosticService.Edge:
                if (string.IsNullOrWhiteSpace(s.EdgeVoice)) throw new UserFeedbackException("Edge Voice 未配置。", ServiceFailure.Configuration);
                break;
            case DiagnosticService.MiMo:
                if (s.MiMoModel == "mimo-v2.5-tts-voiceclone")
                    throw new UserFeedbackException("尚未满足检测条件：VoiceClone 需要主动选择合法参考样本并确认上传。本诊断不选择或上传样本，请使用音频工作台。", ServiceFailure.Configuration);
                MiMoSpeech.GetStyle(s); MiMoSpeech.SplitCreativeText(TestText, s);
                break;
            case DiagnosticService.OpenAiTts:
                if (s.OpenAiModel is "tts-1" or "tts-1-hd" && !string.IsNullOrWhiteSpace(s.OpenAiInstructions))
                    throw new UserFeedbackException("当前模型不支持 Instructions，请清空后保存。", ServiceFailure.Configuration);
                break;
            case DiagnosticService.Llm: ChatCompletionClient.Validate(Question, s, snapshot.Key); break;
        }
    }
    internal async Task<DiagnosticResult> RunAsync(DiagnosticSnapshot snapshot, CancellationToken cancellation)
    {
        if (!gate.Wait(0))
            throw new InvalidOperationException("已有检测正在执行；未排队或发送新请求。");
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(timeout);
        try
        {
            // WinRT voice enumeration and synthesis initiation also stay off the WPF thread.
            await Task.Run(async () => {
                budget.Token.ThrowIfCancellationRequested(); Precheck(snapshot);
                if (snapshot.Service == DiagnosticService.Llm)
                {
                    var text = await answer(snapshot, budget.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text)) throw new UserFeedbackException("服务返回空回答。");
                }
                else if (snapshot.RequestSettings.UsesMiMoStreaming)
                    await ConsumeStreamAsync(snapshot, budget.Token).ConfigureAwait(false);
                else
                {
                    using var audio = await synthesize(snapshot, TestText, budget.Token).ConfigureAwait(false);
                    using var bytes = new MemoryStream(); var block = new byte[16384]; int count;
                    while ((count = await audio.ReadAsync(block, budget.Token).ConfigureAwait(false)) != 0)
                    {
                        if (bytes.Length + count > HttpSpeech.MaxAudioBytes) throw new UserFeedbackException("音频响应超过大小上限。", ServiceFailure.ResponseTooLarge);
                        bytes.Write(block, 0, count);
                    }
                    using var wave = HttpSpeech.ValidatedWave(bytes.ToArray(), budget.Token);
                }
                budget.Token.ThrowIfCancellationRequested();
            }, budget.Token).ConfigureAwait(false);
            return Result(DiagnosticState.Succeeded, "本次调用成功：完整有效响应。未验证长期可用性或回答 / 听觉质量。");
        }
        catch (OperationCanceledException) { return cancellation.IsCancellationRequested
            ? Result(DiagnosticState.Canceled, "检测已取消。") : Result(DiagnosticState.Failed, "检测超时，未重试。", ServiceFailure.Timeout); }
        catch (UserFeedbackException ex) { return Result(DiagnosticState.Failed, ex.Message, ex.Failure, ex.HttpStatus); }
        catch (System.Net.Http.HttpRequestException) { return Result(DiagnosticState.Failed, "连接失败，未重试。", ServiceFailure.Connection); }
        catch (IOException) { return Result(DiagnosticState.Failed, "响应读取中断，未重试。", ServiceFailure.Connection); }
        catch (Exception) { return Result(DiagnosticState.Failed, "服务调用或响应处理失败；未重试。", ServiceFailure.InvalidResponse); }
        finally { gate.Release(); }
        DiagnosticResult Result(DiagnosticState state, string message, ServiceFailure? failure = null, int? status = null) =>
            new(state, message, clock.Elapsed, DateTimeOffset.Now, failure, status);
    }
    private async Task ConsumeStreamAsync(DiagnosticSnapshot snapshot, CancellationToken token)
    {
        using var buffer = new StreamingPcmBuffer(); using var reader = buffer.CreateReader();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = Produce(); var block = new byte[16384];
        try
        {
            while (!producer.IsCompleted || !reader.IsDrained)
            {
                lifetime.Token.ThrowIfCancellationRequested(); reader.Read(block);
                if (buffer.ProducedBytes > HttpSpeech.MaxAudioBytes) throw new UserFeedbackException("PCM 超过大小上限。", ServiceFailure.ResponseTooLarge);
                if (!reader.IsDrained) await Task.Delay(5, lifetime.Token).ConfigureAwait(false);
            }
            await producer.ConfigureAwait(false);
            if (buffer.ProducedBytes == 0 || buffer.ProducedBytes % 2 != 0 || !reader.IsDrained)
                throw new UserFeedbackException("流未产生完整、非空的 PCM 音频。");
        }
        finally { lifetime.Cancel(); try { await producer.ConfigureAwait(false); } catch { /* Original failure remains authoritative. */ } }
        async Task Produce() { try { await stream(snapshot, buffer, lifetime.Token).ConfigureAwait(false); } finally { buffer.Complete(); } }
    }
    private static Task<Stream> Synthesize(DiagnosticSnapshot s, string text, CancellationToken token) => s.Service switch {
        DiagnosticService.Azure => new AzureSpeech(readKey: () => s.Key).SynthesizeAsync(text, s.RequestSettings, token),
        DiagnosticService.MiMo => new MiMoSpeech(readKey: () => s.Key).SynthesizeAsync(text, s.RequestSettings, token),
        DiagnosticService.OpenAiTts => new OpenAiSpeech(readKey: () => s.Key).SynthesizeAsync(text, s.RequestSettings, token),
        _ => new SpeechAudio().SynthesizeAsync(text, s.RequestSettings, token)
    };
}
