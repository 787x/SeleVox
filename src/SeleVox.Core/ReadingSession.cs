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
public interface IStreamingSpeechSynthesis
{
    bool UsesStreaming(Settings settings);
    Task SynthesizeStreamingAsync(string text, Settings settings, StreamingPcmBuffer buffer, CancellationToken cancellation);
}
public interface IStreamingPlaybackFactory
{
    IAudioPlayback CreateStreamingPlayback(StreamingPcmBuffer buffer, Settings settings);
}

public sealed class ReadingSession(ISpeechSynthesis speech, IAudioPlaybackFactory outputs) : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim synthesisGate = new(1, 1);
    private CancellationTokenSource? current;
    private IAudioPlayback? playback;
    private TaskCompletionSource? playbackCompletion;
    private long generation;
    private bool disposed;
    private bool automatic;
    private StreamingPcmBuffer? streamingBuffer;
    private CancellationTokenSource? streamingProducer;
    private bool streamingStarted, streamingInterrupted;
    private string? streamingError;
    public Status Status { get; private set; } = new(ReadingState.Idle, "就绪");
    public event Action<Status>? Changed;

    public Task ReadTextAsync(string text, Settings settings) => ReadAsync(_ => Task.FromResult(text), settings, true);

    public Task ReadCapturedTextAsync(CapturedText text, Settings settings) =>
        string.IsNullOrWhiteSpace(text.Text) ? Task.CompletedTask : ReadAsync(_ => Task.FromResult(text.Text), settings);

    // Unlike the ordinary read entry point, this completes only after actual playback finishes.
    public Task ReadSentenceAsync(string text, Settings settings, CancellationToken cancellation) =>
        ReadAsync(_ => Task.FromResult(text), settings, false, true, cancellation);

    public void CancelPendingAutomatic()
    {
        lock (sync)
        {
            if (disposed || !automatic || current is null) return;
            if (streamingBuffer is not null && streamingStarted && playback is not null)
            {
                // Keep already-started playback, but stop uploads/append immediately and drain only accepted PCM.
                var accepted = streamingBuffer; var producer = streamingProducer;
                streamingInterrupted = true; accepted.Complete();
                SetStatus(Status.State, "自动流式合成已取消，仅继续播放已缓冲音频"); producer?.Cancel(); return;
            }
            if (playback is not null && streamingBuffer is null) return;
            ++generation; var pending = current; current = null;
            if (streamingBuffer is not null) ReleasePlayback();
            SetStatus(ReadingState.Idle, "自动朗读已取消");
            pending.Cancel();
        }
    }

    public async Task ReadAsync(Func<CancellationToken, Task<string>> capture, Settings settings, bool isAutomatic = false, bool waitForPlayback = false, CancellationToken cancellation = default)
    {
        CancellationTokenSource request;
        TaskCompletionSource? completion = null;
        long id;
        lock (sync)
        {
            if (disposed) return;
            // Reject before changing ownership, generation, status or cancellation of the existing session.
            if (isAutomatic && !settings.AllowsAutomaticProvider) return;
            automatic = isAutomatic;
            var cleanupError = CancelAndRelease();
            if (cleanupError is not null)
            {
                SetStatus(ReadingState.Error, $"停止旧音频失败（{cleanupError.GetType().Name}），请重试。");
                if (waitForPlayback) throw new UserFeedbackException(Status.Message);
                return;
            }
            request = current = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            if (waitForPlayback) completion = playbackCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            id = ++generation;
            SetStatus(ReadingState.Synthesizing, "正在获取选区并合成…");
        }
        using var registration = cancellation.Register(() => { lock (sync) { if (id == generation) Stop(); } });
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
                if (speech is IStreamingSpeechSynthesis streaming && streaming.UsesStreaming(settings) && outputs is IStreamingPlaybackFactory streamingOutputs)
                {
                    await ReadStreamingAsync(text, settings, id, request, streaming, streamingOutputs).ConfigureAwait(false);
                    return;
                }
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
            if (completion is not null) await completion.Task.WaitAsync(request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { if (waitForPlayback) throw; }
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
            if (waitForPlayback) throw;
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
            _ = completion?.Task.Exception; // Streaming has its own completion path; observe any device error.
        }
    }

    private async Task ReadStreamingAsync(string text, Settings settings, long id, CancellationTokenSource request,
        IStreamingSpeechSynthesis speechSource, IStreamingPlaybackFactory outputSource)
    {
        using var buffer = new StreamingPcmBuffer();
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAudioPlayback? candidate = null; Task? production = null;
        try
        {
            candidate = await Task.Run(() => outputSource.CreateStreamingPlayback(buffer, settings), request.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested) return;
                streamingBuffer = buffer; streamingProducer = producerCancellation; streamingStarted = streamingInterrupted = false; streamingError = null;
                playback = candidate; candidate = null; var owned = playback;
                owned.Finished += error => _ = Task.Run(() => { Complete(id, owned, error); completed.TrySetResult(); });
                if (owned is MultiOutputPlayback group) group.WarningChanged += () => _ = Task.Run(() => UpdateWarning(id, owned));
                SetStatus(ReadingState.Synthesizing, WithWarning((settings.MiMoOverallStyles ?? []).Any(MiMoStyleTags.IsSinging)
                    ? "MiMo 唱歌：等待首段完整音频，首次生成可能较慢…" : "MiMo 流式缓冲中…", owned));
            }
            production = Task.Run(async () =>
            {
                try
                {
                    await speechSource.SynthesizeStreamingAsync(text, settings, buffer, producerCancellation.Token).ConfigureAwait(false);
                    producerCancellation.Token.ThrowIfCancellationRequested(); buffer.Complete();
                }
                catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested) { buffer.Complete(); }
                catch (Exception error)
                {
                    lock (sync)
                    {
                        if (!disposed && id == generation && !request.IsCancellationRequested &&
                            !(producerCancellation.IsCancellationRequested && streamingInterrupted))
                        {
                            streamingError = error is UserFeedbackException ? $"MiMo 流中断：{error.Message}" : "MiMo 流中断，请检查网络及音频设备。";
                            if (streamingStarted && playback is not null)
                                SetStatus(Status.State, WithWarning(streamingError + " 仅继续播放已缓冲音频。", playback));
                            else { request.Cancel(); ReleasePlayback(); SetStatus(ReadingState.Error, streamingError); }
                        }
                    }
                    buffer.Complete();
                }
            });
            await buffer.WaitForReadyAsync(request.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || id != generation || request.IsCancellationRequested || playback is null) return;
                streamingStarted = true; playback.Play();
                SetStatus(ReadingState.Playing, WithWarning("正在流式朗读（缓冲不足时等待音频）", playback));
            }
            await production.ConfigureAwait(false);
            await completed.Task.WaitAsync(request.Token).ConfigureAwait(false);
            if (Status.State == ReadingState.Error) throw new UserFeedbackException(Status.Message);
        }
        catch
        {
            lock (sync) { if (id == generation) { request.Cancel(); ReleasePlayback(); } }
            throw;
        }
        finally
        {
            producerCancellation.Cancel();
            if (production is not null) await production.ConfigureAwait(false);
            candidate?.Dispose();
            lock (sync)
            {
                if (ReferenceEquals(streamingBuffer, buffer)) { streamingBuffer = null; streamingProducer = null; streamingStarted = false; }
            }
        }
    }

    public void TogglePause()
    {
        lock (sync)
        {
            if (disposed || playback is null) return;
            try
            {
                if (Status.State == ReadingState.Playing) { playback.Pause(); SetStatus(ReadingState.Paused, WithWarning(streamingError is null ? "已暂停" : streamingError + " 已暂停缓冲音频。", playback)); }
                else if (Status.State == ReadingState.Paused) { playback.Play(); SetStatus(ReadingState.Playing, WithWarning(streamingError is null ? "正在朗读" : streamingError + " 仅继续播放已缓冲音频。", playback)); }
            }
            catch (Exception ex)
            {
                var pending = streamingBuffer is not null ? current : null;
                ReleasePlayback(); SetStatus(ReadingState.Error, $"音频操作失败（{ex.GetType().Name}）。"); pending?.Cancel();
            }
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
            if (error is null && streamingBuffer is not null && streamingError is not null) error = new UserFeedbackException(streamingError);
            if (error is null && streamingBuffer is not null && !streamingBuffer.IsComplete)
                error = new UserFeedbackException("流式音频未完成，输出已提前停止。");
            var pending = error is not null && streamingBuffer is not null ? current : null;
            var completion = playbackCompletion; playbackCompletion = null;
            ReleasePlayback();
            if (error is null) completion?.TrySetResult(); else completion?.TrySetException(error);
            SetStatus(error is null ? ReadingState.Idle : ReadingState.Error,
                error is null ? WithWarning(streamingBuffer is not null && streamingInterrupted ? "自动流式合成已取消，已缓冲音频播放结束" : "朗读完成", source) :
                streamingBuffer is not null && streamingError is not null ? streamingError + " 已缓冲音频播放结束，未重试。" : "所有音频输出均失败，请检查设备连接后重试。");
            pending?.Cancel();
        }
    }
    private static string WithWarning(string message, IAudioPlayback source) => source.Warning is { } warning ? $"{message} · {warning}" : message;
    private void UpdateWarning(long id, IAudioPlayback source)
    {
        lock (sync)
        {
            if (disposed || id != generation || !ReferenceEquals(playback, source)) return;
            var message = streamingError is not null ? streamingError + " 仅继续播放已缓冲音频。" :
                streamingBuffer is not null && streamingInterrupted ? "自动流式合成已取消，仅继续播放已缓冲音频" :
                Status.State == ReadingState.Paused ? "已暂停" : Status.State == ReadingState.Synthesizing ? "MiMo 流式缓冲中…" : "正在朗读";
            SetStatus(Status.State, WithWarning(message, source));
        }
    }
    private Exception? CancelAndRelease()
    {
        current?.Cancel(); current = null;
        streamingBuffer = null; streamingProducer = null; streamingStarted = streamingInterrupted = false; streamingError = null;
        return ReleasePlayback();
    }
    private Exception? ReleasePlayback()
    {
        playbackCompletion?.TrySetCanceled(); playbackCompletion = null;
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
