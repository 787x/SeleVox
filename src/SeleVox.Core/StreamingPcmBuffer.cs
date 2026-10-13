namespace SeleVox.Core;

// MiMo's PCM16LE / 24 kHz / mono stream. One bounded ring, independent device cursors.
public sealed class StreamingPcmBuffer : IDisposable
{
    public const int BytesPerSecond = 48000;
    private readonly object sync = new();
    private readonly byte[] ring;
    private readonly int prebuffer;
    private readonly HashSet<Reader> readers = [];
    private TaskCompletionSource changed = NewSignal();
    private long written;
    private bool complete, disposed;
    public long ProducedBytes { get { lock (sync) return written; } }
    public int PeakBufferedBytes { get; private set; }
    public int Capacity => ring.Length;
    public bool IsComplete { get { lock (sync) return complete; } }
    public StreamingPcmBuffer(int capacity = BytesPerSecond * 5, int prebuffer = BytesPerSecond / 5)
    {
        if (capacity < 2 || capacity % 2 != 0 || prebuffer < 2 || prebuffer > capacity || prebuffer % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        ring = new byte[capacity]; this.prebuffer = prebuffer;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var old = changed; changed = NewSignal(); old.TrySetResult(); }
    public Reader CreateReader()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (written != 0) throw new InvalidOperationException("Readers must subscribe before production.");
            var reader = new Reader(this); readers.Add(reader); return reader;
        }
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellation)
    {
        if (pcm.Length % 2 != 0) throw new UserFeedbackException("流式 PCM 音频帧未对齐。");
        var offset = 0;
        while (offset < pcm.Length)
        {
            cancellation.ThrowIfCancellationRequested(); Task wait;
            lock (sync)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (complete) throw new OperationCanceledException("Streaming production ended.", cancellation);
                if (readers.Count == 0) throw new UserFeedbackException("所有流式音频输出均不可用。");
                var retained = written - readers.Min(r => r.position);
                var count = (int)Math.Min(pcm.Length - offset, ring.Length - retained);
                if (count > 0)
                {
                    var index = (int)(written % ring.Length); var first = Math.Min(count, ring.Length - index);
                    pcm.Span.Slice(offset, first).CopyTo(ring.AsSpan(index));
                    pcm.Span.Slice(offset + first, count - first).CopyTo(ring);
                    written += count; offset += count;
                    PeakBufferedBytes = Math.Max(PeakBufferedBytes, (int)(retained + count)); Pulse(); continue;
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }
    public async Task WaitForReadyAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested(); Task wait;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (written >= prebuffer || complete && written > 0) return;
                if (complete) throw new UserFeedbackException("MiMo 流未产生可播放音频。");
                wait = changed.Task;
            }
            await wait.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }
    public void Complete() { lock (sync) { if (disposed) return; complete = true; Pulse(); } }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; complete = true; Pulse(); } }

    public sealed class Reader : IDisposable
    {
        private readonly StreamingPcmBuffer owner;
        internal long position;
        private bool disposed;
        public long ConsumedBytes { get { lock (owner.sync) return position; } }
        public bool IsDrained { get { lock (owner.sync) return owner.complete && position == owner.written; } }
        public long SilenceBytes { get; private set; }
        internal Reader(StreamingPcmBuffer owner) => this.owner = owner;
        public int Read(Span<byte> destination)
        {
            lock (owner.sync)
            {
                if (disposed || owner.disposed) return 0;
                var requested = destination.Length & ~1;
                var available = (int)Math.Min(requested, owner.written - position);
                var index = (int)(position % owner.ring.Length); var first = Math.Min(available, owner.ring.Length - index);
                owner.ring.AsSpan(index, first).CopyTo(destination);
                owner.ring.AsSpan(0, available - first).CopyTo(destination[first..]);
                position += available;
                if (available > 0) owner.Pulse();
                if (owner.complete) return available;
                // Live underflow is temporary: silence keeps WASAPI alive without advancing PCM position.
                destination.Slice(available, requested - available).Clear(); SilenceBytes += requested - available;
                return requested;
            }
        }
        public void Dispose()
        {
            lock (owner.sync) { if (disposed) return; disposed = true; owner.readers.Remove(this); owner.Pulse(); }
        }
    }
}
