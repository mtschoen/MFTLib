using Microsoft.Extensions.Time.Testing;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Splits one chosen write after its 4-byte length prefix: the prefix reaches the peer, then
///     the rest waits on <see cref="Gate" /> or, with <c>failAfterPrefix</c>, the write throws
///     <see cref="IOException" />. Writes before and after the chosen one pass through.
/// </summary>
internal sealed class SplitFrameWrite(int splitWriteNumber, bool failAfterPrefix = false)
{
    int _writes;

    /// <summary>Entered once the chosen write's prefix has been written; released to finish it.</summary>
    public TestGate Gate { get; } = new();

    public Stream Wrap(Stream inner) => new SplitFrameWriteStream(inner, this);

    async ValueTask WriteAsync(Stream inner, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _writes) != splitWriteNumber)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            return;
        }

        await inner.WriteAsync(buffer[..4], cancellationToken);
        await inner.FlushAsync(cancellationToken);
        Gate.MarkEntered();
        if (failAfterPrefix)
        {
            throw new IOException("The pipe broke inside a frame.");
        }

        await Gate.WaitForReleaseAsync(cancellationToken);
        await inner.WriteAsync(buffer[4..], cancellationToken);
    }

    sealed class SplitFrameWriteStream(Stream inner, SplitFrameWrite split) : DelegatingStream(inner)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            split.WriteAsync(Inner, buffer, cancellationToken);
    }
}

/// <summary>
///     Streams it wraps start every write and never finish it, as a pipe whose reader stopped
///     reading does; each write waits until its token is cancelled. Counts the writes started.
/// </summary>
internal sealed class HeldWrites
{
    int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public Stream Wrap(Stream inner) => new HeldWriteStream(inner, this);

    sealed class HeldWriteStream(Stream inner, HeldWrites held) : DelegatingStream(inner)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref held._attempts);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}

/// <summary>Streams it wraps throw <see cref="IOException" /> on every write; their disposal is recorded.</summary>
internal sealed class FailingWrites
{
    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a wrapped stream has been disposed.</summary>
    public Task Disposed => _disposed.Task;

    public Stream Wrap(Stream inner) => new FailingWriteStream(inner, _disposed);

    sealed class FailingWriteStream(Stream inner, TaskCompletionSource disposed) : DelegatingStream(inner)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The drive pipe refused the write."));

        protected override void Dispose(bool disposing)
        {
            disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Passes everything through and records disposal.</summary>
internal sealed class DisposalRecordingStream(Stream inner) : DelegatingStream(inner)
{
    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Disposed => _disposed.Task;

    protected override void Dispose(bool disposing)
    {
        _disposed.TrySetResult();
        base.Dispose(disposing);
    }
}

/// <summary>Closes its inner stream, then throws from <see cref="DisposeAsync" />, as a close that reports a failure does.</summary>
internal sealed class ThrowOnAsyncDisposeStream(Stream inner) : DelegatingStream(inner)
{
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        throw new IOException("control close failed");
    }
}

/// <summary>Closes its inner stream, then throws from <see cref="Dispose(bool)" />, as a synchronous close that reports a failure does.</summary>
internal sealed class ThrowOnDisposeStream(Stream inner) : DelegatingStream(inner)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        throw new IOException("control close failed");
    }
}

/// <summary>A clock whose timers fail when disposed, as a stall timer that cannot drain does.</summary>
internal sealed class ThrowOnTimerDisposeClock : FakeTimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new ThrowOnDisposeTimer(base.CreateTimer(callback, state, dueTime, period));

    sealed class ThrowOnDisposeTimer(ITimer inner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose() => inner.Dispose();

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw new IOException("timer drain failed");
        }
    }
}

/// <summary>
///     Corrupts one chosen write by replacing its frame kind byte with one no frame has, so the host
///     reading it ends its session with <see cref="InvalidDataException" />: a host session fault.
/// </summary>
internal sealed class CorruptFrameWrite(int corruptWriteNumber)
{
    const byte UnknownKind = 200;
    int _writes;

    public Stream Wrap(Stream inner) => new CorruptFrameWriteStream(inner, this);

    ReadOnlyMemory<byte> Corrupt(ReadOnlyMemory<byte> buffer)
    {
        if (Interlocked.Increment(ref _writes) != corruptWriteNumber)
        {
            return buffer;
        }

        var corrupted = buffer.ToArray();
        corrupted[4] = UnknownKind;
        return corrupted;
    }

    sealed class CorruptFrameWriteStream(Stream inner, CorruptFrameWrite corrupt) : DelegatingStream(inner)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Inner.WriteAsync(corrupt.Corrupt(buffer), cancellationToken);
    }
}

/// <summary>
///     Counts the bytes the streams it wraps have delivered to their reader, so a test knows that a
///     frame it wrote has been consumed, not merely sent, and whether the reader is now blocked in a
///     read of the wrapped stream.
/// </summary>
internal sealed class ReadCounter
{
    readonly Lock _gate = new();
    readonly List<(long Bytes, TaskCompletionSource Reached)> _readWaiters = [];
    readonly List<(long Bytes, TaskCompletionSource Reached)> _pendingWaiters = [];
    long _read;

    // The byte count at which the read now blocked in the wrapped stream began, or -1 when none is.
    long _pendingAfter = -1;

    public Stream Wrap(Stream inner) => new CountingStream(inner, this);

    /// <summary>The bytes the wrapped streams have delivered to their reader so far.</summary>
    public long BytesRead
    {
        get
        {
            lock (_gate)
            {
                return _read;
            }
        }
    }

    /// <summary>Completes once at least <paramref name="bytes" /> bytes have been read in total.</summary>
    public Task WhenRead(long bytes) => Wait(_readWaiters, bytes, () => _read >= bytes);

    /// <summary>
    ///     Completes once, after at least <paramref name="bytes" /> bytes were read, a read has reached
    ///     the wrapped stream and found nothing to return: the wrapped stream holds that read
    ///     pending, so the reader is blocked in it rather than about to call it.
    /// </summary>
    public Task WhenReadPendingAfter(long bytes) => Wait(_pendingWaiters, bytes, () => _pendingAfter >= bytes);

    Task Wait(List<(long Bytes, TaskCompletionSource Reached)> waiters, long bytes, Func<bool> reached)
    {
        lock (_gate)
        {
            if (reached())
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((bytes, signal));
            return signal.Task;
        }
    }

    void MarkPending()
    {
        lock (_gate)
        {
            _pendingAfter = _read;
            Release(_pendingWaiters, _read);
        }
    }

    void Add(int count)
    {
        lock (_gate)
        {
            _pendingAfter = -1;
            _read += count;
            Release(_readWaiters, _read);
        }
    }

    // Continuations run asynchronously, so completing the signals under the lock runs no test code there.
    static void Release(List<(long Bytes, TaskCompletionSource Reached)> waiters, long read)
    {
        foreach (var waiter in waiters.Where(waiter => read >= waiter.Bytes))
        {
            waiter.Reached.TrySetResult();
        }

        waiters.RemoveAll(waiter => read >= waiter.Bytes);
    }

    sealed class CountingStream(Stream inner, ReadCounter counter) : DelegatingStream(inner)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Inner.ReadAsync(buffer, cancellationToken);
            if (!read.IsCompleted)
            {
                counter.MarkPending();
            }

            var count = await read;
            counter.Add(count);
            return count;
        }
    }
}
