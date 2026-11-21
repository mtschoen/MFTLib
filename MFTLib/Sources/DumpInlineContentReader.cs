using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Reads inline content through the open dump file that produced this reader's block, so the bytes always come
///     from the file the block's rows were parsed from: renaming, deleting or replacing the path changes nothing here,
///     while a write into that same file is read as it is now. The block and every outstanding lease each hold one
///     reference; the file closes when the last is returned.
/// </summary>
sealed class DumpInlineContentReader(MftDumpInput input) : IBlockContentReader
{
    int _references = 1;
    int _blockReleased;

    public Task ReadAsync(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken) =>
        Task.Run(() => Read(requests, sink, cancellationToken), cancellationToken);

    public IDisposable Lease()
    {
        var references = Volatile.Read(ref _references);
        while (true)
        {
            ObjectDisposedException.ThrowIf(references == 0, this);
            var observed = Interlocked.CompareExchange(ref _references, references + 1, references);
            if (observed == references)
            {
                return new ReaderLease(this);
            }

            references = observed;
        }
    }

    /// <summary>The block's release: the file closes now, or when the last outstanding lease is returned.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _blockReleased, 1) == 0)
        {
            Release();
        }
    }

    void Read(ReadOnlyMemory<InlineContentRequest> requests, InlineContentSink sink,
        CancellationToken cancellationToken)
    {
        using var reader = input.OpenInlineContentReader();
        reader.Read(requests.Span, sink, cancellationToken);
    }

    void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            input.Dispose();
        }
    }

    /// <summary>One reference, returned once however often it is disposed.</summary>
    sealed class ReaderLease(DumpInlineContentReader owner) : IDisposable
    {
        int _returned;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
