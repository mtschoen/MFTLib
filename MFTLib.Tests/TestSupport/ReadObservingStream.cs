namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Passes every call through to <paramref name="inner" /> and counts the reads handed to it, so
///     a test can act while a reader is provably parked in a read that no data has answered yet.
/// </summary>
public sealed class ReadObservingStream(Stream inner) : Stream
{
    readonly Lock _lock = new();
    readonly List<(int Count, TaskCompletionSource Issued)> _waiters = [];
    int _readsIssued;

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Completes once <paramref name="count" /> ReadAsync calls have been handed to the inner stream.</summary>
    public Task ReadsIssued(int count)
    {
        lock (_lock)
        {
            if (_readsIssued >= count)
            {
                return Task.CompletedTask;
            }

            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, issued));
            return issued.Task;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = inner.ReadAsync(buffer, cancellationToken);
        List<TaskCompletionSource> reached;
        lock (_lock)
        {
            _readsIssued++;
            reached = _waiters.Where(waiter => waiter.Count <= _readsIssued).Select(waiter => waiter.Issued).ToList();
            _waiters.RemoveAll(waiter => waiter.Count <= _readsIssued);
        }

        foreach (var issued in reached)
        {
            issued.TrySetResult();
        }

        return read;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return inner.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return inner.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
