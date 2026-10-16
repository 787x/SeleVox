using System.Text;
using System.Threading.Channels;

namespace SeleVox.Core;

public enum QuestionSource { Manual, MouseAutomatic, OcrAutomatic }
public enum QuestionState { Idle, Generating, Speaking, Completed, Cancelled, Error }
public sealed record QuestionStatus(string Question, string Service, string Model, string Answer, QuestionState State, string Message, bool AnswerComplete = false, long RequestId = 0);

// One foreground question. UI polls Snapshot at a bounded rate, rather than dispatching every token.
public sealed class QuestionSession(
    Func<string, Settings, string, Action<string>, CancellationToken, Task<string>> generate,
    Func<string, Settings, Task> read, Action stopSpeech,
    Func<string, Settings, CancellationToken, Task>? readSentence = null) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? current;
    private long generation;
    private long requestId;
    private bool disposed, ownsSpeech, answerComplete;
    private QuestionSource source;
    public QuestionSource Source { get { lock (sync) return source; } }
    private readonly StringBuilder answer = new();
    private string question = "", service = "", model = "", message = "";
    private QuestionState state;
    public QuestionStatus Snapshot
    { get { lock (sync) return new(question, service, model, answer.ToString(), state, message, answerComplete, requestId); } }

    public Task AskAsync(string text, Settings settings, string key, bool isAutomatic = false)
        => AskAsync(text, settings, key, isAutomatic ? QuestionSource.MouseAutomatic : QuestionSource.Manual);

    public async Task AskAsync(string text, Settings settings, string key, QuestionSource origin)
    {
        if (!AllowsSource(settings, origin)) return;
        ChatCompletionClient.Validate(text, settings, key); // Invalid actions leave the current task/audio intact.
        CancellationTokenSource request; long id;
        lock (sync)
        {
            if (disposed) return;
            CancelCore(); stopSpeech(); ownsSpeech = false;
            request = current = new(); id = ++generation;
            ++requestId;
            source = origin;
            question = text; service = Settings.NormalizeServiceUrl(settings.LlmBaseUrl)!; model = settings.LlmModel;
            answer.Clear(); answerComplete = false; state = QuestionState.Generating; message = "思考中 / 等待最终正文…";
        }
        var canRead = settings.LlmAutoRead && (origin == QuestionSource.Manual || settings.AllowsAutomaticProvider);
        var incremental = canRead && settings.LlmStreaming && settings.LlmReadingStrategy == AnswerReadingStrategy.Incremental && readSentence is not null;
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var voiceCancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        var sentences = new AnswerSentenceBuffer();
        string? speechError = null;
        void Enqueue(string sentence)
        {
            if (voiceCancellation.IsCancellationRequested) { request.Token.ThrowIfCancellationRequested(); return; }
            try { queue.Writer.WriteAsync(sentence, voiceCancellation.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { request.Token.ThrowIfCancellationRequested(); }
        }
        var consumer = incremental ? Task.Run(async () =>
        {
            try
            {
                await foreach (var sentence in queue.Reader.ReadAllAsync(voiceCancellation.Token).ConfigureAwait(false))
                {
                    Task speech;
                    lock (sync)
                    {
                        if (id != generation || disposed) return;
                        voiceCancellation.Token.ThrowIfCancellationRequested();
                        ownsSpeech = true; message = answerComplete ? "回答已生成 · 语音排队 / 播放中" : "正在生成回答 · 语音排队 / 播放中";
                        speech = readSentence!(sentence, settings, voiceCancellation.Token);
                    }
                    await speech.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (voiceCancellation.IsCancellationRequested) { }
            catch (Exception)
            {
                speechError = "回答朗读不完整，请检查 TTS 或设备；可主动重新朗读完整回答。";
                voiceCancellation.Cancel();
            }
        }) : Task.CompletedTask;
        try
        {
            Task<string> Produce() => generate(text, settings, key, delta =>
            {
                lock (sync)
                {
                    if (disposed || id != generation || request.IsCancellationRequested) return;
                    if (answer.Length + delta.Length > ChatCompletionClient.MaxText) throw new UserFeedbackException("AI 回答过长。");
                    answer.Append(delta); message = incremental ? "正在生成回答 · 语音排队 / 播放中" : "正在生成回答…";
                }
                if (incremental) foreach (var sentence in sentences.Append(delta)) Enqueue(sentence);
            }, request.Token);
            // Bounded backpressure can wait for playback: never execute this producer on WPF's UI thread.
            var complete = await (incremental ? Task.Run(Produce) : Produce()).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(complete) || complete.Length > ChatCompletionClient.MaxText)
                    throw new UserFeedbackException("AI 返回空回答或回答过长。");
                answerComplete = true;
            }
            if (incremental)
            {
                foreach (var sentence in sentences.Append("", true)) Enqueue(sentence);
                queue.Writer.TryComplete();
                lock (sync) if (id == generation && !request.IsCancellationRequested) { state = QuestionState.Speaking; message = "回答已生成 · 等待语音播放完成"; }
                await consumer.ConfigureAwait(false);
            }
            Task? speech = null;
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(complete) || complete.Length > ChatCompletionClient.MaxText)
                    throw new UserFeedbackException("AI 返回空回答或回答过长。");
                answer.Clear(); answer.Append(complete); state = QuestionState.Completed; message = speechError ?? "回答完成";
                // Starting ReadingSession synchronously under the same ownership lock closes the Stop/completion race.
                if (canRead && !incremental) { ownsSpeech = true; state = QuestionState.Speaking; message = "回答已生成 · 语音播放中"; speech = read(complete, settings); }
            }
            if (speech is not null)
            {
                try { await speech.ConfigureAwait(false); }
                catch (Exception) { speechError = "回答朗读不完整，可主动重新朗读完整回答。"; }
                lock (sync) if (id == generation && !request.IsCancellationRequested) { state = QuestionState.Completed; message = speechError ?? "回答完成"; }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (!disposed && id == generation && !request.IsCancellationRequested)
                {
                    voiceCancellation.Cancel(); if (ownsSpeech) { ownsSpeech = false; stopSpeech(); }
                    state = QuestionState.Error;
                    message = "回答不完整：" + (ex is UserFeedbackException ? ex.Message : "AI 问答失败；已保留正文，未自动重试。");
                }
            }
        }
        finally
        {
            voiceCancellation.Cancel(); queue.Writer.TryComplete(); await consumer.ConfigureAwait(false);
            lock (sync) { if (ReferenceEquals(current, request)) current = null; }
            request.Dispose();
        }
    }
    public async Task ReadAnswerAsync(Settings settings)
    {
        long id = -1;
        try
        {
            Task speech;
            lock (sync)
            {
                if (disposed || !answerComplete || state is QuestionState.Generating or QuestionState.Speaking || answer.Length == 0) return;
                CancelCore(); source = QuestionSource.Manual; ownsSpeech = true; id = generation; speech = read(answer.ToString(), settings);
            }
            await speech.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            lock (sync) if (!disposed && id == generation) message = "回答朗读不完整，请检查 TTS 或设备后主动重读。";
        }
    }
    public void CancelAutomatic() { lock (sync) { if (source != QuestionSource.Manual) CancelCore(); } }
    public void CancelMouseAutomatic() { lock (sync) { if (source == QuestionSource.MouseAutomatic) CancelCore(); } }
    public static bool AllowsSource(Settings settings, QuestionSource origin) => origin switch
    {
        QuestionSource.Manual => true,
        QuestionSource.MouseAutomatic => settings.AutoAsk && settings.AllowsAutomaticLlm,
        QuestionSource.OcrAutomatic => settings.AllowsOcrLlm,
        _ => false
    };
    public void CancelOcr() { lock (sync) { if (source == QuestionSource.OcrAutomatic) CancelCore(); } }
    public void Cancel()
    {
        lock (sync) { CancelCore(); }
    }
    private void CancelCore()
    {
        ++generation; var old = current; current = null; old?.Cancel();
        if (ownsSpeech) { ownsSpeech = false; stopSpeech(); }
        if (state is QuestionState.Generating or QuestionState.Speaking) { state = QuestionState.Cancelled; message = "已取消，部分回答未自动朗读"; }
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; CancelCore(); } }
}
