namespace MFTLib.Index;

/// <summary>
///     The block's region geometry and its write cursors. Capacities and offsets are copied once,
///     from a header already validated against the mapped length, when the block is built; every
///     span and every writer bounds check uses that copy and never the mapped header. A broker
///     writes into a section whose header a less trusted process can rewrite at any moment, so a
///     header value is something this block publishes, not something it trusts. The row count and
///     the used name-pool length are tracked here and published to the header after each change.
/// </summary>
sealed partial class BlockFile
{
    readonly long _sequenceRegionOffset;
    readonly long _namePoolOffset;
    uint _rowCount;
    uint _namePoolUsed;
    ulong _generation;

    /// <summary>Row slot count from the validated layout this block was built from.</summary>
    internal uint SlotCapacity { get; }

    /// <summary>Name-pool capacity, in bytes, from the validated layout this block was built from.</summary>
    internal uint NamePoolCapacity { get; }

    /// <summary>Highest written row index plus one, as this block's writers have published it.</summary>
    internal uint RowCount => _rowCount;

    /// <summary>Used name-pool bytes, as this block's writers have published them.</summary>
    internal uint NamePoolUsed => _namePoolUsed;

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
        _namePoolUsed = usedBytes;
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
            published.RowCount > SlotCapacity || published.NamePoolUsed > NamePoolCapacity)
        {
            return false;
        }

        _rowCount = published.RowCount;
        _namePoolUsed = published.NamePoolUsed;
        _generation = published.Generation;
        return true;
    }
}
