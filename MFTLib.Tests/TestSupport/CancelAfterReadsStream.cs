namespace MFTLib.Tests.TestSupport;

// Wraps a stream and invokes a callback once its ReadAsync has been called
// `threshold` times - used to inject cancellation deterministically between two
// frame reads instead of racing an already-blocked read.
public sealed class CancelAfterReadsStream : Stream
{
    readonly Stream _inner;
    readonly int _threshold;
    readonly Action _onThreshold;
    int _reads;

    public CancelAfterReadsStream(Stream inner, int threshold, Action onThreshold)
    {
        _inner = inner;
        _threshold = threshold;
        _onThreshold = onThreshold;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (Interlocked.Increment(ref _reads) == _threshold)
            _onThreshold();
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);
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
