namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Wraps a bidirectional stream so that once <see cref="BreakPipe" /> is called every
///     write and flush throws <see cref="IOException" />, the way a named pipe fails when
///     the far end is gone ("Pipe is broken" / ERROR_NO_DATA). Reads pass through untouched,
///     so the wrapper's owner still observes EOF once the peer is disposed.
///     <see cref="DuplexStream" /> cannot produce this failure: disposing one side completes
///     the peer's reader but never makes this side's writes throw.
/// </summary>
public sealed class BrokenPipeStream(Stream inner) : Stream
{
    readonly TaskCompletionSource _writeFailureObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _broken;

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    ///     Completes once a write or flush has been attempted on the broken pipe, so a test
    ///     can order the disconnect deterministically instead of racing the peer's EOF.
    /// </summary>
    public Task WriteFailureObserved => _writeFailureObserved.Task;

    public void BreakPipe()
    {
        _broken = true;
    }

    void ThrowIfBroken()
    {
        if (_broken)
        {
            _writeFailureObserved.TrySetResult();
            throw new IOException("Pipe is broken.");
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfBroken();
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfBroken();
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        ThrowIfBroken();
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfBroken();
        return inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return inner.ReadAsync(buffer, cancellationToken);
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
