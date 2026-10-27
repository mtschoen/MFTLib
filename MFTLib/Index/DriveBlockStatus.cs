namespace MFTLib.Index;

/// <summary>
///     The block behind one drive's <see cref="RootStatus" />, captured with that status. Present
///     only on a <see cref="DriveState.Ready" /> drive.
/// </summary>
public sealed record DriveBlockStatus
{
    internal DriveBlockStatus()
    {
    }

    /// <summary>
    ///     Where the block behind this status came from. Reads <see cref="BlockOrigin.ProducedByScan" /> after a successful
    ///     rescan, because that rescan is what produced the current block.
    /// </summary>
    public BlockOrigin Origin { get; init; }

    /// <summary>
    ///     The current block's cache location, captured with this status. Read
    ///     <see cref="FileIndex.Status" /> again after a rescan for an updated value.
    ///     Null when the block is not backed by a cache location, as under <see cref="CacheMode.Disabled" />.
    ///     This does not identify another owner or promise that a private block's location
    ///     is still held elsewhere. <see cref="Origin" /> independently describes
    ///     how the current block was obtained.
    /// </summary>
    public CacheLocation? CacheLocation { get; init; }

    /// <summary>
    ///     Rows that are in use and not tombstoned: the rows a query can return.
    /// </summary>
    public uint LiveRowCount { get; init; }

    /// <summary>
    ///     UTC time at which the current block's production completed, stamped into its header.
    ///     A block adopted from the cache keeps its original time, so this is the block's age,
    ///     not the time the index opened.
    /// </summary>
    public DateTime ScanTimestamp { get; init; }

    /// <summary>
    ///     The current block's change version. It increases on every applied journal change batch and on
    ///     every rescan, and a warm start adopts the cached block's value. It is comparable only within
    ///     one <see cref="FileIndex" /> instance: a deleted cache or a new process can restart it. A
    ///     consumer remembers the last value it saw for a drive and compares; a different value means
    ///     the drive's rows may have changed.
    /// </summary>
    public ulong Generation { get; init; }

    /// <summary>A producer write or mutation could not fit; the inventory is incomplete or stale and only a rescan repairs it.</summary>
    public bool IsCapacityExhausted { get; init; }

    /// <summary>
    ///     The MFT producer's count of records it could not place during the latest scan:
    ///     unsupported record identifiers, empty names, exhausted block capacity and allocated records
    ///     the native parser omitted because their fixups are invalid. Zero for
    ///     an enumeration-backed drive and after a warm start, because the count is not stored
    ///     in the block.
    /// </summary>
    public int SkippedRecordCount { get; init; }

    /// <summary>
    ///     The enumeration producer's count of subtrees it could not enter during the scan that
    ///     built the block, including directories that vanished before entry. The block stores it,
    ///     so a warm start reports the count of the scan that built it. Zero for an MFT-backed drive.
    /// </summary>
    public int AccessDeniedSubtreeCount { get; init; }
}
