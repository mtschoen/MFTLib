namespace MFTLib.Index;

/// <summary>
///     Where a drive's current block came from, as of the moment the status is read. A consumer
///     running a rebuild loop over an index it just opened uses this to skip the drives the open
///     already scanned, and a cache-management view uses it to show "loaded from cache" against
///     "scanned".
/// </summary>
public enum BlockSource
{
    /// <summary>
    ///     The drive has no block: it was offline at open, or its MFT producer failed.
    /// </summary>
    None,

    /// <summary>
    ///     The block was adopted from a valid file in the cache directory. Its contents are as
    ///     old as <see cref="DriveStatus.ScanTimestamp" /> says.
    /// </summary>
    WarmStartedFromCache,

    /// <summary>
    ///     This index produced the block: a first-ever cold scan during <c>OpenAsync</c>, a cold
    ///     scan after an existing block was rejected (see <see cref="DriveStatus.DiscardedBlock" />),
    ///     or a successful <see cref="FileIndex.RescanAsync" />.
    /// </summary>
    ProducedByScan
}
