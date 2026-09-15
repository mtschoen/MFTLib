namespace MFTLib.Tests.TestSupport;

internal sealed class SingleReaderGuardStream(Stream inner) : Stream
{
    readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _readers;
    public Task FirstReadStarted => _firstRead.Task;
    public bool ConcurrentReadAttempted { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _readers) != 1)
        {
            Interlocked.Decrement(ref _readers);
            ConcurrentReadAttempted = true;
            throw new IOException("Concurrent broker pipe read.");
        }
        try
        {
            _firstRead.TrySetResult();
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        finally { Interlocked.Decrement(ref _readers); }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Use asynchronous reads in this fixture.");
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
