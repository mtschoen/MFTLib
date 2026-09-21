namespace MFTLib.Index;

/// <summary>
///     How to open an index. An empty drive set opens an index with no drives, which is a valid
///     state, not an error.
/// </summary>
public sealed record FileIndexOptions
{
    public IReadOnlyList<IndexedDrive> Drives { get; init; } = [];

    /// <summary>
    ///     Null resolves to <see cref="MFTLib.Index.CacheDirectory.ResolveDefaultPath" />.
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
    ///     <see cref="DriveFailureKind.CacheDeclined" /> (or <see cref="DriveFailureKind.InUse" />
    ///     when another live index holds the cache block's owner lock) instead of falling back to
    ///     a scan, and <see cref="FileIndex.RescanAsync" /> remains available to scan it later.
    ///     A cache block whose journal checkpoint the journal no longer holds is different: this
    ///     open never watches, and the block is still a correct snapshot as of its age, so it is
    ///     adopted instead of declined, with <see cref="DriveStatus.CheckpointLoss" /> set to say
    ///     why the checkpoint could not be resumed. Such a drive is left out of a later
    ///     <see cref="FileIndex.StartWatchingAsync" /> (see <see cref="DriveStatus.WatchFailureMessage" />)
    ///     until <see cref="FileIndex.RescanAsync" /> gives it a fresh cursor.
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
    ///     Supplies the merged watch stream used by <see cref="FileIndex.StartWatchingAsync" />.
    ///     That stream carries a per-drive failure as a <see cref="DriveWatchFailure" /> item and
    ///     faults only for a failure that names no drive. This is also the object a rescan asks to
    ///     disarm and re-arm the one drive it rebuilds. Required when any opened drive has an MFT
    ///     block and ignored when none does.
    /// </summary>
    public IIndexWatchSource? WatchSource { get; init; }

    public IProgress<IndexScanProgress>? Progress { get; init; }

    /// <summary>
    ///     Open-time per-drive progress: <see cref="FileIndex.OpenAsync" /> reports one
    ///     <see cref="IndexDriveOpened" /> per configured drive, in <see cref="Drives" /> order,
    ///     synchronously on the opening thread, after that drive settles, whatever the outcome:
    ///     warm-started, cold-scanned, declined by <see cref="InitialOpenCacheOnly" />, offline,
    ///     or failed. A declined or failed drive still counts toward the total and still reports.
    ///     Null (the default) reports and allocates nothing. Only the initial open reports;
    ///     <see cref="FileIndex.RescanAsync" /> stays silent, because its caller already awaits
    ///     the one drive it rescans. Unlike <see cref="Progress" />, which samples only while a
    ///     producer runs, this fires on warm starts too. Marshalling belongs to the
    ///     <see cref="IProgress{T}" /> implementation, the same convention <see cref="Progress" />
    ///     uses.
    /// </summary>
    public IProgress<IndexDriveOpened>? OpenProgress { get; init; }

    /// <summary>
    ///     Receives one human-readable line for every block-file delete this index performs:
    ///     the deleted path and the reason (a rejected cache validation, a stale ".retired-*"
    ///     sweep, a superseded block's final release, a cancelled scan's partial replacement,
    ///     or a rejected producer block). Invoked synchronously on whatever thread performs the
    ///     delete, which can be a snapshot release inside <see cref="FileIndex.DisposeAsync" />,
    ///     so a subscriber must be fast and non-blocking. Null (the default) logs nothing.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }
}
