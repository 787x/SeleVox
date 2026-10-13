using System.Text;

namespace SeleVox.Core;

public enum QuestionState { Idle, Generating, Completed, Cancelled, Error }
public sealed record QuestionStatus(string Question, string Service, string Model, string Answer, QuestionState State, string Message);

// One foreground question. UI polls Snapshot at a bounded rate, rather than dispatching every token.
public sealed class QuestionSession(
    Func<string, Settings, string, Action<string>, CancellationToken, Task<string>> generate,
    Func<string, Settings, Task> read, Action stopSpeech) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? current;
    private long generation;
    private bool disposed, ownsSpeech;
    private readonly StringBuilder answer = new();
    private string question = "", service = "", model = "", message = "";
    private QuestionState state;
    public QuestionStatus Snapshot
    { get { lock (sync) return new(question, service, model, answer.ToString(), state, message); } }

    public async Task AskAsync(string text, Settings settings, string key)
    {
        ChatCompletionClient.Validate(text, settings, key); // Invalid actions leave the current task/audio intact.
        CancellationTokenSource request; long id;
        lock (sync)
        {
            if (disposed) return;
            CancelCore(); stopSpeech(); ownsSpeech = false;
            request = current = new(); id = ++generation;
            question = text; service = Settings.NormalizeServiceUrl(settings.LlmBaseUrl)!; model = settings.LlmModel;
            answer.Clear(); state = QuestionState.Generating; message = "生成中…";
        }
        try
        {
            var complete = await generate(text, settings, key, delta =>
            {
                lock (sync)
                {
                    if (disposed || id != generation || request.IsCancellationRequested) return;
                    if (answer.Length + delta.Length > ChatCompletionClient.MaxText) throw new UserFeedbackException("AI 回答过长。");
                    answer.Append(delta);
                }
            }, request.Token).ConfigureAwait(false);
            Task? speech = null;
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(complete) || complete.Length > ChatCompletionClient.MaxText)
                    throw new UserFeedbackException("AI 返回空回答或回答过长。");
                answer.Clear(); answer.Append(complete); state = QuestionState.Completed; message = "回答完成";
                // Starting ReadingSession synchronously under the same ownership lock closes the Stop/completion race.
                if (settings.LlmAutoRead) { ownsSpeech = true; speech = read(complete, settings); }
            }
            if (speech is not null) await speech.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (!disposed && id == generation && !request.IsCancellationRequested)
                {
                    state = QuestionState.Error;
                    message = ex is UserFeedbackException ? ex.Message : "AI 问答失败；已保留正文，未自动重试。";
                }
            }
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(current, request)) current = null; }
            request.Dispose();
        }
    }
    public Task ReadAnswerAsync(Settings settings)
    {
        lock (sync)
        {
            if (disposed || state != QuestionState.Completed || answer.Length == 0) return Task.CompletedTask;
            CancelCore(); ownsSpeech = true; return read(answer.ToString(), settings);
        }
    }
    public void Cancel()
    {
        lock (sync) { CancelCore(); }
    }
    private void CancelCore()
    {
        ++generation; var old = current; current = null; old?.Cancel();
        if (ownsSpeech) { ownsSpeech = false; stopSpeech(); }
        if (state == QuestionState.Generating) { state = QuestionState.Cancelled; message = "已取消，部分回答未自动朗读"; }
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; CancelCore(); } }
}
