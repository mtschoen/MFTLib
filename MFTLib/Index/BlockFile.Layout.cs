namespace MFTLib.Index;

/// <summary>
///     The block's region geometry and its write cursors. Capacities and offsets are copied once,
///     from a header already validated against the mapped length, when the block is built; every
///     span and every writer bounds check uses that copy and never the mapped header. A broker
///     writes into a section whose header a less trusted process can rewrite at any moment, so a
///     header value is something this block publishes, not something it trusts. The row count and
///     the used name-pool length and name entry count are tracked here and published to the header after each change.
/// </summary>
sealed partial class BlockFile
{
    readonly long _sequenceRegionOffset;
    readonly long _nameEntryRegionOffset;
    readonly long _bucketRegionOffset;
    readonly long _namePoolOffset;
    uint _rowCount;
    uint _namePoolUsed;
    uint _nameEntryCount;
    ulong _generation;

    /// <summary>Row slot count from the validated layout this block was built from.</summary>
    internal uint SlotCapacity { get; }

    /// <summary>Name-pool capacity, in bytes, from the validated layout this block was built from.</summary>
    internal uint NamePoolCapacity { get; }

    /// <summary>Allocatable name entries, excluding reserved entry zero, from the validated layout.</summary>
    internal uint NameEntryCapacity { get; }

    /// <summary>Hash bucket heads from the validated layout.</summary>
    internal uint BucketCount { get; }

    /// <summary>Published name entries, excluding reserved entry zero.</summary>
    internal uint NameEntryCount => Volatile.Read(ref _nameEntryCount);

    /// <summary>Records a new name entry count and publishes it to the header.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> exceeds <see cref="NameEntryCapacity" />.</exception>
    internal void PublishNameEntryCount(uint count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, NameEntryCapacity);
        Volatile.Write(ref _nameEntryCount, count);
        Header.NameEntryCount = count;
    }

    /// <summary>Highest written row index plus one, as this block's writers have published it.</summary>
    internal uint RowCount => _rowCount;

    /// <summary>Used name-pool bytes, as this block's writers have published them.</summary>
    internal uint NamePoolUsed => Volatile.Read(ref _namePoolUsed);

    /// <summary>The change version, as this block's writers have published it; never re-read from the mapped header.</summary>
    internal ulong Generation => _generation;

    /// <summary>Records a new change version and publishes it to the header.</summary>
    internal void PublishGeneration(ulong generation)
    {
        _generation = generation;
        Header.Generation = generation;
    }

    /// <summary>Records a new row count and publishes it to the header.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount" /> exceeds <see cref="SlotCapacity" />.</exception>
    internal void PublishRowCount(uint rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, SlotCapacity);
        _rowCount = rowCount;
        Header.RowCount = rowCount;
    }

    /// <summary>Records a new used name-pool length and publishes it to the header.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usedBytes" /> exceeds <see cref="NamePoolCapacity" />.</exception>
    internal void PublishNamePoolUsed(uint usedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(usedBytes, NamePoolCapacity);
        // Release: readers that acquire the entry count or a bucket head after this see the used length.
        Volatile.Write(ref _namePoolUsed, usedBytes);
        Header.NamePoolUsed = usedBytes;
    }

    /// <summary>
    ///     Takes the cursors another process published after writing this block through its own
    ///     mapping of the same section, as the client does once the broker reports the scan ready.
    ///     The copy must describe this block's own capacities and stay within them, so a header
    ///     claiming more than the block holds is refused instead of adopted.
    /// </summary>
    /// <param name="published">A private copy of the header the other writer published.</param>
    /// <returns>true when the cursors were adopted; otherwise, false and the cursors are unchanged.</returns>
    internal bool TryAdoptPublishedCursors(BlockHeader published)
    {
        if (published.SlotCapacity != SlotCapacity || published.NamePoolCapacity != NamePoolCapacity ||
            published.NameEntryCapacity != NameEntryCapacity || published.BucketCount != BucketCount ||
            published.NameEntryCount > NameEntryCapacity || published.RowCount > SlotCapacity || published.NamePoolUsed > NamePoolCapacity)
        {
            return false;
        }

        _rowCount = published.RowCount;
        _namePoolUsed = published.NamePoolUsed;
        Volatile.Write(ref _nameEntryCount, published.NameEntryCount);
        _generation = published.Generation;
        return true;
    }
}
