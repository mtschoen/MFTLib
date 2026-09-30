namespace MFTLib.Index;

/// <summary>
///     How to open an index. An empty drive set opens an index with no drives, which is a valid
///     state, not an error.
/// </summary>
public sealed record FileIndexOptions
{
    public IReadOnlyList<IndexedDrive> Drives { get; init; } = [];

    /// <summary>
    ///     Null resolves to <see cref="MFTLib.Index.CacheDirectory.ResolveDefaultPath()" />.
    ///     Test hosts that activate <c>CacheDirectoryIsolation.ForbidDefaultCacheDirectory</c>
    ///     must supply a temporary path; null then causes <see cref="FileIndex.OpenAsync" />
    ///     to throw <see cref="InvalidOperationException" /> before directory creation.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    ///     Creates each block in the temp directory instead of the cache directory. The file is
    ///     created with <see cref="FileOptions.DeleteOnClose" />, so the operating system removes
    ///     the temp file when the last handle closes. This includes process exit by kill rather
    ///     than graceful dispose, so no stale no-cache file is left behind.
    /// </summary>
    public bool NoCache { get; init; }

    /// <summary>
    ///     Opens each drive from its cache only. A drive with no usable cache (missing, corrupt,
    ///     or incompatible) is reported as <see cref="DriveState.Failed" /> with
    ///     <see cref="DriveFailureKind.CacheDeclined" />, <see cref="DriveFailureKind.CacheTagMismatch" />
    ///     (or <see cref="DriveFailureKind.InUse" /> when another live index holds the cache block's
    ///     owner lock) instead of falling back to a scan, and <see cref="FileIndex.RescanAsync(char, CancellationToken)" />
    ///     remains available to scan it later.
    ///     A cache block whose journal checkpoint the journal no longer holds is different: this
    ///     open never watches, and the block is still a correct snapshot as of its age, so it is
    ///     adopted instead of declined, with <see cref="DriveStatus.CheckpointLoss" /> set to say
    ///     why the checkpoint could not be resumed. Such a drive is left out of a later
    ///     <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> (see <see cref="DriveStatus.WatchFailureMessage" />)
    ///     until <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> gives it a fresh cursor.
    /// </summary>
    public bool InitialOpenCacheOnly { get; init; }

    public ProducerPolicy ProducerPolicy { get; init; } = ProducerPolicy.Mft;

    /// <summary>
    ///     Supplies MFT-derived blocks. Null is a configuration error when
    ///     <see cref="FileIndexOptions.ProducerPolicy" /> is
    ///     <see cref="MFTLib.Index.ProducerPolicy.Mft" /> and is ignored when it is
    ///     <see cref="MFTLib.Index.ProducerPolicy.Enumeration" />.
    /// </summary>
    public MftBlockProducer? MftProducer { get; init; }

    /// <summary>
    ///     Starts each drive's own watch for <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" />, one
    ///     <see cref="IIndexDriveWatch" /> handle per drive. A rescan of a watched drive stops that
    ///     drive's handle and starts a fresh one from the new block's cursor through the same
    ///     source. Required to watch an MFT-backed drive and ignored otherwise.
    /// </summary>
    public IIndexWatchSource? WatchSource { get; init; }

    /// <summary>
    ///     Samples from whichever producer is building a drive's block. Each drive scan, in
    ///     <see cref="FileIndex.OpenAsync" /> and in a rescan alike, ends with exactly one
    ///     <see cref="IndexScanPhase.Finished" /> sample whose <see cref="IndexScanProgress.Outcome" />
    ///     says whether it succeeded, failed or was cancelled. Drives scan concurrently, so
    ///     samples of different drives interleave. A handler that throws on the Finished sample is
    ///     contained and reported to <see cref="Diagnostics" />, so it never replaces the scan's own
    ///     exception or result; a throw on any other sample fails that scan like any producer fault. Marshalling belongs to the
    ///     <see cref="IProgress{T}" /> implementation.
    /// </summary>
    public IProgress<IndexScanProgress>? Progress { get; init; }

    /// <summary>
    ///     Open-time per-drive progress: <see cref="FileIndex.OpenAsync" /> reports one
    ///     <see cref="IndexDriveOpened" /> for each drive that settles, synchronously on the thread
    ///     that settled it, whatever the outcome: warm-started, cold-scanned, declined by
    ///     <see cref="InitialOpenCacheOnly" />, offline, or failed. A drive whose settle is
    ///     cancelled does not settle and reports nothing, so a cancelled or failed open may have
    ///     reported only some of the configured drives. Drives settle concurrently and no lock is held while a handler runs,
    ///     so reports can overlap and can arrive out of order; a handler that blocks holds up no
    ///     other drive. <see cref="IndexDriveOpened.SettledCount" /> gives each report's place in
    ///     settle order, so keep the report with the largest count rather than the last one
    ///     received. A declined or failed drive still counts toward the total and still reports.
    ///     Null (the default) reports and allocates nothing. Only the initial open reports;
    ///     <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> stays silent, because its caller already awaits
    ///     the one drive it rescans. Unlike <see cref="Progress" />, which samples only while a
    ///     producer runs, this fires on warm starts too. Marshalling belongs to the
    ///     <see cref="IProgress{T}" /> implementation, the same convention <see cref="Progress" />
    ///     uses.
    /// </summary>
    public IProgress<IndexDriveOpened>? OpenProgress { get; init; }

    /// <summary>
    ///     Receives human-readable block-file deletion lines and cache-tag mismatch lines.
    ///     A tag mismatch includes both stored and requested tags even when deletion fails.
    ///     Invoked synchronously on whatever thread performs the delete or encounters a mismatch,
    ///     which can be a snapshot release inside <see cref="FileIndex.DisposeAsync" />,
    ///     so a subscriber must be fast and non-blocking. Null (the default) logs nothing.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>
    ///     An optional consumer cache tag identifying the scan shape (profile and keep-list).
    ///     The tag is validated on construction: the FourCC must contain exactly four ASCII characters.
    ///     The default value is all zeros, meaning unspecified. Comparison matches both the FourCC and
    ///     version exactly. The consumer owns and increments its version whenever its scan shape changes.
    ///     MFTLib does not interpret the tag or compare scan filters.
    /// </summary>
    public CacheTag CacheTag { get; init; }

    /// <summary>
    ///     A test seam, owned by the index these options open: lists the cache directory's files
    ///     matching a pattern for the sweep of stale ".retired-*" siblings. Null (the default)
    ///     lists the real directory. The directory holds the index's owner lock file while the
    ///     sweep runs, so only a failure of the listing itself (a network share that drops, an
    ///     access-control change made meanwhile) reaches the sweep's handlers, and a test needs
    ///     this to stand in for one. Supplied through the options so the index is never handed
    ///     to test code before it has settled.
    /// </summary>
    internal Func<string, string, IEnumerable<string>>? EnumerateCacheFilesForTest { get; init; }
}
