namespace MFTLibTestExtensions;

/// <summary>
///     One end of an in-memory pipe: reads come from the peer's writes. Disposing it completes
///     both directions, so the peer's reads reach EOF, and a later read or write on this end throws
///     <see cref="ObjectDisposedException" />, as a closed named pipe does.
/// </summary>
internal sealed class InMemoryDuplexStream(Stream read, Stream write) : DelegatingStream(read)
{
    volatile bool _closed;

    public override bool CanWrite => true;

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        write.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return write.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return base.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        // Disposing completes the pipe's reader, and a read already in flight then reports it as
        // misuse; a closed pipe reports a read cut off by its own close as ObjectDisposedException.
        catch (InvalidOperationException) when (_closed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        write.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return write.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            write.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Every write waits on the task <c>hold</c> returns before any byte of it is written.</summary>
internal sealed class HeldWriteStream(Stream inner, Func<Task> hold) : DelegatingStream(inner)
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await hold().WaitAsync(cancellationToken).ConfigureAwait(false);
        await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
///     The client's end of the in-process control pipe. Closing it is EOF to the host; disposing
///     it asynchronously also waits until the host has exited, which never faults: a failed host
///     session reaches the client only as its pipes closing.
/// </summary>
internal sealed class HostLifetimeStream(Stream client, Task hostExited) : DelegatingStream(client)
{
    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync().ConfigureAwait(false);
        await hostExited.ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
