namespace SeleVox.Core;

public enum ReadingState { Idle, Synthesizing, Playing, Paused, Error }
public sealed record Status(ReadingState State, string Message);
public sealed class UserFeedbackException(string message) : Exception(message);

public interface IAudioPlayback : IDisposable
{
    event Action<Exception?>? Finished;
    void Play();
    void Pause();
    void Stop();
}

// A synthesized stream belongs to the caller; a playback owns it after creation.
public interface ISpeechAudio
{
    Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation);
    IAudioPlayback CreatePlayback(Stream audio, float volume);
}

public sealed class ReadingSession(ISpeechAudio speech) : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim synthesisGate = new(1, 1);
    private CancellationTokenSource? current;
    private IAudioPlayback? playback;
    private long generation;
    private bool disposed;
    public Status Status { get; private set; } = new(ReadingState.Idle, "就绪");
    public event Action<Status>? Changed;

    public async Task ReadAsync(Func<CancellationToken, Task<string>> capture, Settings settings)
    {
        CancellationTokenSource request;
        long id;
        lock (sync)
        {
            if (disposed) return;
            var cleanupError = CancelAndRelease();
            if (cleanupError is not null) { SetStatus(ReadingState.Error, $"停止旧音频失败（{cleanupError.GetType().Name}），请重试。"); return; }
            request = current = new();
            id = ++generation;
            SetStatus(ReadingState.Synthesizing, "正在获取选区并合成…");
        }
        Stream? audio = null;
        IAudioPlayback? candidate = null;
        try
        {
            var text = await capture(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new UserFeedbackException("没有选中文字。");
            await synthesisGate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                audio = await speech.SynthesizeAsync(text, settings, request.Token).ConfigureAwait(false);
            }
            finally { synthesisGate.Release(); }
            request.Token.ThrowIfCancellationRequested();
            candidate = speech.CreatePlayback(audio, settings.Volume);
            audio = null;
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested) return;
                playback = candidate;
                candidate = null;
                var owned = playback;
                owned.Finished += error => _ = Task.Run(() => Complete(id, owned, error));
                owned.Play();
                SetStatus(ReadingState.Playing, "正在朗读");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (!disposed && id == generation)
                {
                    ReleasePlayback();
                    SetStatus(ReadingState.Error, ex is UserFeedbackException ? ex.Message : $"朗读失败（{ex.GetType().Name}）。请检查本地语音及默认音频设备。");
                }
            }
        }
        finally
        {
            candidate?.Dispose();
            audio?.Dispose();
            lock (sync) { if (ReferenceEquals(current, request)) current = null; }
            request.Dispose();
        }
    }

    public void TogglePause()
    {
        lock (sync)
        {
            if (disposed || playback is null) return;
            try
            {
                if (Status.State == ReadingState.Playing) { playback.Pause(); SetStatus(ReadingState.Paused, "已暂停"); }
                else if (Status.State == ReadingState.Paused) { playback.Play(); SetStatus(ReadingState.Playing, "正在朗读"); }
            }
            catch (Exception ex) { ReleasePlayback(); SetStatus(ReadingState.Error, $"音频操作失败（{ex.GetType().Name}）。"); }
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            if (disposed) return;
            ++generation;
            var error = CancelAndRelease();
            SetStatus(error is null ? ReadingState.Idle : ReadingState.Error,
                error is null ? "已停止" : $"停止音频失败（{error.GetType().Name}）。");
        }
    }

    private void Complete(long id, IAudioPlayback source, Exception? error)
    {
        lock (sync)
        {
            if (disposed || id != generation || !ReferenceEquals(source, playback)) return;
            ReleasePlayback();
            SetStatus(error is null ? ReadingState.Idle : ReadingState.Error,
                error is null ? "朗读完成" : $"音频设备错误（{error.GetType().Name}）。");
        }
    }
    private Exception? CancelAndRelease() { current?.Cancel(); current = null; return ReleasePlayback(); }
    private Exception? ReleasePlayback()
    {
        var old = playback;
        playback = null;
        if (old is null) return null;
        Exception? failure = null;
        try { old.Stop(); } catch (Exception ex) { failure = ex; }
        try { old.Dispose(); } catch (Exception ex) { failure ??= ex; }
        return failure;
    }
    private void SetStatus(ReadingState state, string message) { Status = new(state, message); Changed?.Invoke(Status); }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; ++generation; CancelAndRelease(); }
        // A non-cooperative provider may still finish later; do not dispose its gate while it is in use.
    }
}
