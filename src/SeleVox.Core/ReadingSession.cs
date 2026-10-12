namespace SeleVox.Core;

public enum ReadingState { Idle, Synthesizing, Playing, Paused, Error }
public sealed record Status(ReadingState State, string Message, bool Notify = true);
public sealed class UserFeedbackException(string message) : Exception(message);

public interface IAudioPlayback : IDisposable
{
    string? Warning => null;
    event Action<Exception?>? Finished;
    void Play();
    void Pause();
    void Stop();
}

// A synthesized stream belongs to the caller; a playback owns it after creation.
public interface ISpeechSynthesis
{
    Task<Stream> SynthesizeAsync(string text, Settings settings, CancellationToken cancellation);
}
public interface IAudioPlaybackFactory
{
    IAudioPlayback CreatePlayback(Stream audio, Settings settings);
}

public sealed class ReadingSession(ISpeechSynthesis speech, IAudioPlaybackFactory outputs) : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim synthesisGate = new(1, 1);
    private CancellationTokenSource? current;
    private IAudioPlayback? playback;
    private long generation;
    private bool disposed;
    private bool automatic;
    public Status Status { get; private set; } = new(ReadingState.Idle, "就绪");
    public event Action<Status>? Changed;

    public Task ReadTextAsync(string text, Settings settings) => ReadAsync(_ => Task.FromResult(text), settings, true);

    public void CancelPendingAutomatic()
    {
        lock (sync)
        {
            if (disposed || !automatic || current is null || playback is not null) return;
            ++generation; current.Cancel(); current = null;
            SetStatus(ReadingState.Idle, "自动朗读已取消");
        }
    }

    public async Task ReadAsync(Func<CancellationToken, Task<string>> capture, Settings settings, bool isAutomatic = false)
    {
        CancellationTokenSource request;
        long id;
        lock (sync)
        {
            if (disposed) return;
            automatic = isAutomatic;
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
            if (isAutomatic && settings.Provider == TtsProvider.Azure && !settings.AllowAutomaticCloud)
                throw new UserFeedbackException("自动向 Azure 发送选区未授权，请在设置中单独开启；手动朗读仍可使用。");
            await synthesisGate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                audio = await speech.SynthesizeAsync(text, settings, request.Token).ConfigureAwait(false);
            }
            finally { synthesisGate.Release(); }
            request.Token.ThrowIfCancellationRequested();
            candidate = await Task.Run(() => outputs.CreatePlayback(audio, settings)).ConfigureAwait(false);
            audio = null;
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested) return;
                playback = candidate;
                candidate = null;
                var owned = playback;
                owned.Finished += error => _ = Task.Run(() => Complete(id, owned, error));
                if (owned is MultiOutputPlayback group)
                    group.WarningChanged += () => _ = Task.Run(() => UpdateWarning(id, owned));
                owned.Play();
                SetStatus(ReadingState.Playing, WithWarning("正在朗读", owned));
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
                    SetStatus(ReadingState.Error, ex is UserFeedbackException ? ex.Message : $"朗读失败（{ex.GetType().Name}）。请检查语音服务及输出设备。");
                }
            }
        }
        finally
        {
            Exception? cleanupError = null;
            try { candidate?.Dispose(); } catch (Exception ex) { cleanupError = ex; }
            try { audio?.Dispose(); } catch (Exception ex) { cleanupError ??= ex; }
            lock (sync)
            {
                if (cleanupError is not null && !disposed && id == generation)
                    SetStatus(ReadingState.Error, "音频资源释放失败，请重启应用后重试。");
            }
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
                if (Status.State == ReadingState.Playing) { playback.Pause(); SetStatus(ReadingState.Paused, WithWarning("已暂停", playback)); }
                else if (Status.State == ReadingState.Paused) { playback.Play(); SetStatus(ReadingState.Playing, WithWarning("正在朗读", playback)); }
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
                error is null ? WithWarning("朗读完成", source) : "所有音频输出均失败，请检查设备连接后重试。");
        }
    }
    private static string WithWarning(string message, IAudioPlayback source) => source.Warning is { } warning ? $"{message} · {warning}" : message;
    private void UpdateWarning(long id, IAudioPlayback source)
    {
        lock (sync)
        {
            if (disposed || id != generation || !ReferenceEquals(playback, source)) return;
            SetStatus(Status.State, WithWarning(Status.State == ReadingState.Paused ? "已暂停" : "正在朗读", source));
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
    private void SetStatus(ReadingState state, string message) { Status = new(state, message, !automatic); Changed?.Invoke(Status); }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; ++generation; CancelAndRelease(); }
        // A non-cooperative provider may still finish later; do not dispose its gate while it is in use.
    }
}
