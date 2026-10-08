namespace MFTLib.Index;

/// <summary>
///     The block behind one drive's <see cref="DriveStatus" />, captured with that status. Every
///     value is zero for a drive with no block.
/// </summary>
public sealed record DriveBlockStatus
{
    internal DriveBlockStatus()
    {
    }

    /// <summary>
    ///     Where the block behind this status came from. <see cref="BlockSource.None" /> for a
    ///     drive with no block. Reads <see cref="BlockSource.ProducedByScan" /> after a successful
    ///     rescan, because that rescan is what produced the current block.
    /// </summary>
    public BlockSource Source { get; init; }

    /// <summary>
    ///     The current block's cache-slot backing, captured with this status. Read
    ///     <see cref="FileIndex.Drives" /> again after a rescan for an updated value.
    ///     <see cref="CacheSlotState.NotApplicable" /> for NoCache or blockless drives.
    ///     This does not identify another owner or promise that a private block's slot
    ///     is still held elsewhere. <see cref="Source" /> independently describes
    ///     how the current block was obtained.
    /// </summary>
    public CacheSlotState CacheSlot { get; init; }

    /// <summary>
    ///     Rows that are in use and not tombstoned: the rows a query can return. Zero for a
    ///     drive with no block.
    /// </summary>
    public uint LiveRowCount { get; init; }

    /// <summary>
    ///     UTC time at which the current block's production completed, stamped into its header.
    ///     A block adopted from the cache keeps its original time, so this is the block's age,
    ///     not the time the index opened. <see cref="DateTime.MinValue" /> for a drive with no block.
    /// </summary>
    public DateTime ScanTimestamp { get; init; }

    /// <summary>
    ///     The block header's compaction flag: a mutation or a producer write could not fit, so
    ///     the block is incomplete or stale and only a rescan repairs it.
    /// </summary>
    public bool CompactionNeeded { get; init; }

    /// <summary>
    ///     The MFT producer's count of records it could not place during the latest scan:
    ///     unsupported record identifiers, empty names and exhausted block capacity. Zero for
    ///     an enumeration-backed drive and after a warm start, because the count is not stored
    ///     in the block.
    /// </summary>
    public int SkippedRecordCount { get; init; }

    /// <summary>
    ///     The enumeration producer's count of subtrees it could not enter during the latest
    ///     scan, including directories that vanished before entry. Zero for an MFT-backed
    ///     drive and after a warm start, because the count is not stored in the block.
    /// </summary>
    public int AccessDeniedSubtreeCount { get; init; }
}
