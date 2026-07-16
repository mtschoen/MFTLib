namespace MFTLib.Tests.TestSupport;

// Wraps a stream and blocks the write call for one specific frame kind until
// Release() is called, so a test can deterministically pause a client operation
// mid-flight (holding the client's write lock) to race a concurrent state
// transition against it, then let it proceed. The frame kind byte sits at offset
// 4 of the buffer passed to WriteAsync (4-byte length prefix, then kind) since
// JournalBrokerClient writes one frame per WriteAsync call.
public sealed class GateFrameWriteStream : Stream
{
    readonly Stream _inner;
    readonly byte _gatedKind;
    readonly int _gatedOccurrence;
    int _occurrenceCount;
    readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // occurrence selects which send of gatedKind to gate (1-based); a frame kind sent
    // more than once (e.g. ArmAndScan on both the initial scan and a rescan) can gate
    // just the later send while earlier ones pass straight through.
    public GateFrameWriteStream(Stream inner, BrokerFrameKind gatedKind, int occurrence = 1)
    {
        _inner = inner;
        _gatedKind = (byte)gatedKind;
        _gatedOccurrence = occurrence;
    }

    // Completes once the gated frame's write call has started (and is blocked).
    public Task Entered => _entered.Task;

    // Releases the gated write. Idempotent; any write arriving after this point
    // (including the gated kind again) proceeds immediately.
    public void Release() => _gate.TrySetResult();

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length >= 5 && buffer.Span[4] == _gatedKind
            && Interlocked.Increment(ref _occurrenceCount) == _gatedOccurrence)
        {
            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
        }
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
