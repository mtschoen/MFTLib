using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Wraps a bidirectional stream so that once <see cref="BreakPipe" /> is called every
///     write and flush throws <see cref="IOException" />, the way a named pipe fails when
///     the far end is gone ("Pipe is broken" / ERROR_NO_DATA). Reads pass through untouched,
///     so the wrapper's owner still observes EOF once the peer is disposed.
///     <see cref="DuplexStream" /> cannot produce this failure: disposing one side completes
///     the peer's reader but never makes this side's writes throw.
/// </summary>
internal sealed class BrokenPipeStream(Stream inner) : DelegatingStream(inner)
{
    readonly TaskCompletionSource _writeFailureObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _broken;

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
        Inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfBroken();
        return Inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        ThrowIfBroken();
        Inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfBroken();
        return Inner.FlushAsync(cancellationToken);
    }
}
