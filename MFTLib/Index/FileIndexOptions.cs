namespace MFTLib.Index;

/// <summary>
///     How to open an index. An empty drive set opens an index with no drives, which is a valid
///     state, not an error.
/// </summary>
public sealed record FileIndexOptions
{
    public IReadOnlyList<IndexedDrive> Drives { get; init; } = [];

    /// <summary>Null resolves to <see cref="CacheDirectory.ResolveDefaultPath" />.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    ///     Creates each block in the temp directory instead of the cache directory. The file is
    ///     created with <see cref="FileOptions.DeleteOnClose" />, so the operating system removes
    ///     the temp file when the last handle closes. This includes process exit by kill rather
    ///     than graceful dispose, so no stale no-cache file is left behind.
    /// </summary>
    public bool NoCache { get; init; }

    public ProducerPolicy ProducerPolicy { get; init; } = ProducerPolicy.Mft;

    /// <summary>
    ///     Supplies MFT-derived blocks. Null is a configuration error when
    ///     <see cref="FileIndexOptions.ProducerPolicy" /> is
    ///     <see cref="MFTLib.Index.ProducerPolicy.Mft" /> and is ignored when it is
    ///     <see cref="MFTLib.Index.ProducerPolicy.Enumeration" />.
    /// </summary>
    public MftBlockProducer? MftProducer { get; init; }

    /// <summary>
    ///     Supplies the merged watch stream used by <see cref="FileIndex.StartWatchingAsync" />.
    ///     That stream carries a per-drive failure as a <see cref="DriveWatchFailure" /> item and
    ///     faults only for a failure that names no drive. This is also the object a rescan asks to
    ///     disarm and re-arm the one drive it rebuilds. Required when any opened drive has an MFT
    ///     block and ignored when none does.
    /// </summary>
    public IIndexWatchSource? WatchSource { get; init; }

    public IProgress<IndexScanProgress>? Progress { get; init; }
}
