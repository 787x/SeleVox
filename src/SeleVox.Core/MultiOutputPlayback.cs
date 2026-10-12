namespace SeleVox.Core;

// Every child owns an independent reader. Callbacks are queued so a WASAPI worker
// never waits for disposal of its own thread or enters ReadingSession's lock.
public sealed class MultiOutputPlayback : IAudioPlayback
{
    private sealed class Output(IAudioPlayback player)
    {
        public readonly IAudioPlayback Player = player;
        public Action<Exception?> Handler = null!;
        public bool Ended;
    }
    private readonly object sync = new();
    private readonly Output[] outputs;
    private readonly List<string> warnings = [];
    private int successful;
    private bool disposed, stopped, completed;
    public event Action<Exception?>? Finished;
    public event Action? WarningChanged;
    public string? Warning { get { lock (sync) return warnings.Count == 0 ? null : string.Join("；", warnings.Distinct()); } }

    public MultiOutputPlayback(IEnumerable<IAudioPlayback> players, string? warning = null)
    {
        outputs = players.Select(p => new Output(p)).ToArray();
        if (outputs.Length == 0) throw new UserFeedbackException("没有可用的音频输出设备。");
        if (warning is not null) warnings.Add(warning);
        foreach (var output in outputs)
        {
            output.Handler = error => _ = Task.Run(() => End(output, error));
            output.Player.Finished += output.Handler;
        }
    }
    public void Play() => Apply(p => p.Play());
    public void Pause() => Apply(p => p.Pause());
    private void Apply(Action<IAudioPlayback> action)
    {
        lock (sync)
        {
            if (disposed || stopped || completed) return;
            foreach (var output in outputs.Where(o => !o.Ended))
            {
                try { action(output.Player); }
                catch (Exception error) { End(output, error); }
            }
        }
    }
    private void End(Output output, Exception? error)
    {
        lock (sync)
        {
            if (disposed || stopped || output.Ended) return;
            output.Ended = true;
            output.Player.Finished -= output.Handler;
            try { output.Player.Dispose(); }
            catch { error ??= new IOException(); }
            if (error is null) ++successful;
            else { warnings.Add("部分输出设备失败或断开，其他有效输出继续"); WarningChanged?.Invoke(); }
            if (!completed && outputs.All(o => o.Ended))
            {
                completed = true;
                Finished?.Invoke(successful > 0 ? null : new UserFeedbackException("所有音频输出均失败。"));
            }
        }
    }
    public void Stop()
    {
        lock (sync)
        {
            if (disposed || stopped) return;
            stopped = true;
            Exception? failure = null;
            foreach (var output in outputs.Where(o => !o.Ended))
            {
                output.Player.Finished -= output.Handler;
                try { output.Player.Stop(); } catch (Exception ex) { failure ??= ex; }
            }
            if (failure is not null) throw new UserFeedbackException("停止某个音频输出失败，将尝试释放所有输出。");
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            Exception? failure = null;
            try { Stop(); } catch (Exception ex) { failure = ex; }
            disposed = true;
            foreach (var output in outputs.Where(o => !o.Ended))
            {
                output.Ended = true;
                try { output.Player.Dispose(); } catch (Exception ex) { failure ??= ex; }
            }
            if (failure is not null) throw new UserFeedbackException("某个音频输出资源释放失败，请重启应用后重试。");
        }
    }
}
