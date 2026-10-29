namespace MFTLib.Index;

/// <summary>
///     The per-name posting regions: an immutable base in compressed-sparse-row form built when a producer completes the
///     block, and an append-only delta chain for names the journal changes afterwards. A posting is a hint that a reader
///     verifies against the row's descriptor, so nothing is ever unlinked. See <c>docs/index-format.md</c>.
/// </summary>
sealed unsafe partial class BlockFile
{
    readonly long _postingOffsetRegionOffset;
    readonly long _postingRowRegionOffset;
    readonly long _deltaHeadRegionOffset;
    readonly long _deltaEntryRegionOffset;
    uint _postingBaseNameCount;
    uint _postingBaseRowCount;
    uint _deltaEntryCount;
    volatile bool _postingsBuilt;

    /// <summary>Allocatable delta entries, excluding reserved entry zero, from the validated layout.</summary>
    internal uint DeltaCapacity { get; }

    /// <summary>Names that existed when the posting base was built, as this block's writers have published it.</summary>
    internal uint PostingBaseNameCount => Volatile.Read(ref _postingBaseNameCount);

    /// <summary>Row indices held by the posting base, as this block's writers have published it.</summary>
    internal uint PostingBaseRowCount => Volatile.Read(ref _postingBaseRowCount);

    /// <summary>Published delta entries, excluding reserved entry zero.</summary>
    internal uint DeltaEntryCount => Volatile.Read(ref _deltaEntryCount);

    /// <summary>
    ///     Whether the posting base covers every row. Set when a producer completes the block, when a complete block is
    ///     opened and when a broker-completed section is adopted; a journal name change appends a delta entry only then.
    /// </summary>
    internal bool PostingsBuilt => _postingsBuilt;

    /// <summary>
    ///     The posting offsets: <see cref="NameEntryCapacity" /> plus two words. Name <c>n</c> owns base slots
    ///     <c>[offsets[n], offsets[n + 1])</c>; words 0 and 1 are zero.
    /// </summary>
    internal Span<uint> PostingOffsets
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CountNameRegionAccess();
            return new Span<uint>(_base + _postingOffsetRegionOffset, (int)NameEntryCapacity + 2);
        }
    }

    /// <summary>The posting rows, <see cref="SlotCapacity" /> words long: row indices grouped by name, ascending within a name.</summary>
    internal Span<uint> PostingRows
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CountNameRegionAccess();
            return new Span<uint>(_base + _postingRowRegionOffset, (int)SlotCapacity);
        }
    }

    /// <summary>The delta heads, one per name bucket, published with volatile stores. Zero is an empty chain.</summary>
    internal Span<uint> DeltaHeads
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CountNameRegionAccess();
            return new Span<uint>(_base + _deltaHeadRegionOffset, (int)BucketCount);
        }
    }

    /// <summary>The delta entries, <see cref="DeltaCapacity" /> plus one long, including reserved entry zero.</summary>
    internal Span<DeltaEntry> DeltaEntries
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CountNameRegionAccess();
            return new Span<DeltaEntry>(_base + _deltaEntryRegionOffset, (int)DeltaCapacity + 1);
        }
    }

    /// <summary>
    ///     Records a finished posting base and publishes it to the header. The delta chain restarts empty: the base was
    ///     built from every row, so any earlier delta entry is redundant.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A count exceeds the cursor it is bounded by.</exception>
    internal void PublishPostingBase(uint nameCount, uint rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nameCount, NameEntryCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, RowCount);
        Volatile.Write(ref _postingBaseNameCount, nameCount);
        Volatile.Write(ref _postingBaseRowCount, rowCount);
        Volatile.Write(ref _deltaEntryCount, 0);
        Header.PostingBaseNameCount = nameCount;
        Header.PostingBaseRowCount = rowCount;
        Header.DeltaEntryCount = 0;
        _postingsBuilt = true;
    }

    /// <summary>Records a new delta entry count and publishes it to the header.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> exceeds <see cref="DeltaCapacity" />.</exception>
    internal void PublishDeltaEntryCount(uint count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, DeltaCapacity);
        Volatile.Write(ref _deltaEntryCount, count);
        Header.DeltaEntryCount = count;
    }

    /// <summary>
    ///     Checks the cursors another writer published for the posting regions: the delta capacity is the block's own and
    ///     every count stays within the cursor that bounds it.
    /// </summary>
    bool IsWithinPostingBounds(BlockHeader published)
    {
        return published.DeltaCapacity == DeltaCapacity && published.DeltaEntryCount <= DeltaCapacity &&
               published.PostingBaseNameCount <= published.NameEntryCount &&
               published.PostingBaseRowCount <= published.RowCount;
    }

    void AdoptPostingCursors(BlockHeader published)
    {
        Volatile.Write(ref _postingBaseNameCount, published.PostingBaseNameCount);
        Volatile.Write(ref _postingBaseRowCount, published.PostingBaseRowCount);
        Volatile.Write(ref _deltaEntryCount, published.DeltaEntryCount);
        _postingsBuilt = published.IsComplete;
    }
}
