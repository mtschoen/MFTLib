namespace MFTLib.Index;

/// <summary>
///     The client surface over one or more mapped drive blocks. Opening picks a producer per
///     drive, warm-starts from a valid block or cold-scans, and publishes a snapshot. A rescan
///     writes a new block beside the old one and swaps it in; handles minted from the old
///     snapshot keep the old block mapped until they are collected, so a held handle never
///     dangles across a rescan. Split into partial files by responsibility: aggregate construction,
///     admission, and query coordination live on <see cref="FileIndex" />, while drive behavior
///     lives in <c>FileIndex.DriveRuntime.*.cs</c>.
/// </summary>
public sealed partial class FileIndex : IAsyncDisposable
{
    readonly FileIndexOptions _options;

    /// <summary>
    ///     Cancelled by <see cref="DisposeAsync" /> before it waits for anything. Every query's
    ///     effective token is linked to this one, so a scan in flight is told to stop rather than
    ///     holding disposal open for the rest of a whole-drive pass. Every rescan's token is linked
    ///     to it as well. Deliberately not disposed, for the same reason as the drives' gates: a
    ///     query that raced the disposal still reads this token, and a disposed source would
    ///     answer it with an exception naming the source rather than the index.
    /// </summary>
    readonly CancellationTokenSource _disposalCancellation = new();

    /// <summary>
    ///     The signal that stops the queries in flight when this index is disposed. A query's
    ///     effective token is this one, or this one linked with the caller's own.
    /// </summary>
    internal CancellationToken DisposalToken => _disposalCancellation.Token;

    /// <summary>
    ///     Guards every drive's runtime state, and is the one step
    ///     that publishes a snapshot: a commit of a drive's block, the new snapshot, and the
    ///     retirement of the replaced block all happen under it, so a reader (<see cref="Drives" />, <see cref="CurrentSnapshot" />,
    ///     <see cref="TryGetDriveOrdinal" />) never observes a partial swap. It is innermost in the
    ///     lock order (see <see cref="DriveRuntime" />) and never held across an <c>await</c>.
    /// </summary>
    readonly Lock _stateLock = new();

    /// <summary>
    ///     The published drive blocks queries borrow: an immutable array of active blocks
    ///     replaced on every publication, or null once disposal has unpublished it.
    /// </summary>
    DriveBlock[]? _snapshot = [];
    bool _disposed;
    ushort _nextDriveOrdinal;

    /// <summary>
    ///     How many scans of one drive in a row may lose their journal catch-up before a scan
    ///     operation stops rescanning the drive by itself.
    /// </summary>
    internal const int LostCatchUpRecoveryLimit = 3;

    ushort AllocateDriveOrdinalLocked() => _nextDriveOrdinal++;


    FileIndex(FileIndexOptions options, string cacheDirectoryPath, IndexHooks hooks)
    {
        _options = options;
        CacheDirectoryPath = cacheDirectoryPath;
        Hooks = hooks;
        foreach (var configuredDrive in _options.Drives)
        {
            var drive = configuredDrive.Drive.Validated();
            _driveRuntimes.Add(drive, new DriveRuntime(this, configuredDrive));
        }
    }

    /// <summary>
    ///     The explicitly configured cache directory, or empty for <see cref="CacheMode.Disabled" /> and dump opens.
    /// </summary>
    internal string CacheDirectoryPath { get; }

    /// <summary>
    ///     Recomputed from the block headers on every read, so it reflects the latest mutation.
    ///     Ordered to follow <see cref="FileIndexOptions.Drives" />, including blockless drives.
    /// </summary>
    public IReadOnlyList<DriveStatus> Drives
    {
        get
        {
            Hooks.DrivesReadBeforeLock?.Invoke();
            lock (_stateLock)
            {
                // Checked under the lock: disposal sets the flag under it before it unpublishes
                // any block, so a read that sees the flag clear sees every block still published.
                ObjectDisposedException.ThrowIf(_disposed, this);
                var statuses = new List<DriveStatus>(_options.Drives.Count);
                foreach (var configured in _options.Drives)
                {
                    statuses.Add(GetDriveRuntime(configured.Drive).DescribeLocked());
                }

                return statuses;
            }
        }
    }

    internal IReadOnlyList<DriveBlock> CurrentSnapshot
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateLock)
            {
                return _snapshot ?? throw new ObjectDisposedException(nameof(FileIndex));
            }
        }
    }

    /// <summary>
    ///     Opens an index over the configured drives, adopting a valid cached block for each or
    ///     producing a fresh one. The returned task completes once every drive has settled. A
    ///     missing drive settles as <see cref="DriveState.Offline" />, and an operational scan
    ///     failure settles as <see cref="DriveState.Failed" /> in <see cref="Drives" /> without
    ///     failing the open, for both MFT and enumeration producers; this includes an MFT drive that needs
    ///     a scan when <see cref="FileIndexOptions.MftSource" /> is null. Argument validation performed
    ///     before any drive is produced throws. Caller cancellation fails the whole open and releases
    ///     every adopted block.
    /// </summary>
    /// <param name="options">The drives to index and the cache, progress and producer settings.</param>
    /// <param name="cancellationToken">
    ///     Cancels the open. Every drive's settle finishes first, and blocks already adopted are
    ///     released before the cancellation is thrown.
    /// </param>
    /// <returns>The opened index, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="options" /> specifies an undefined <see cref="FileIndexOptions.CacheMode" /> or a drive
    ///     specifies an undefined <see cref="IndexedDrive.Producer" />.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     Cache configuration is missing, dump options are invalid, or <see cref="FileIndexOptions.Drives" /> is null,
    ///     contains a null or invalid drive, or lists a drive more than once.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the open finished.</exception>
    public static Task<FileIndex> OpenAsync(FileIndexOptions options,
        CancellationToken cancellationToken) =>
        OpenWithHooksAsync(options, new IndexHooks(), cancellationToken);

    /// <summary>
    ///     Opens an index with the supplied per-index hooks, invoking open-time dependencies before returning.
    /// </summary>
    internal static async Task<FileIndex> OpenWithHooksAsync(FileIndexOptions options,
        IndexHooks hooks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        options = DriveKind.PrepareOpenOptions(options);
        string cacheDirectoryPath = string.Empty;
        if (options.CacheMode != CacheMode.Disabled)
        {
            var suppliedCacheDirectory = options.CacheDirectory;
            if (string.IsNullOrWhiteSpace(suppliedCacheDirectory))
            {
                throw new ArgumentException(
                    "Set FileIndexOptions.CacheDirectory or set FileIndexOptions.CacheMode to Disabled.",
                    nameof(options));
            }

            cacheDirectoryPath = suppliedCacheDirectory;
            CacheDirectory.EnsureCreated(cacheDirectoryPath);
        }

        var index = new FileIndex(options, cacheDirectoryPath, hooks);
        try
        {
            await index.SettleDrivesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Every drive's settle has finished by now, so a failure or a cancellation must not
            // leak the mappings (and, for a no-cache block, the temp file) that the drives that
            // did settle already adopted.
            index.ReleaseUnpublishedBlocks();
            throw;
        }

        lock (index._stateLock)
        {
            index.PublishSnapshotLocked();
        }

        return index;
    }

    internal bool TryGetDriveOrdinal(DriveKey drive, out ushort driveOrdinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            return TryGetDriveOrdinalLocked(drive, out driveOrdinal);
        }
    }

    /// <summary>
    ///     The same lookup without the disposal check, for the watch paths that run while an index
    ///     is being torn down: recording a drive's failure or clearing it on an index that is going
    ///     away has nothing to do, not a different exception to raise over the one already in
    ///     flight. The caller holds <see cref="_stateLock" />.
    /// </summary>
    bool TryGetDriveOrdinalLocked(DriveKey drive, out ushort driveOrdinal)
    {
        if (_driveRuntimes.TryGetValue(drive.Validated(), out var runtime) && runtime.Block is { } block)
        {
            driveOrdinal = block.DriveOrdinal;
            return true;
        }

        driveOrdinal = 0;
        return false;
    }
}
