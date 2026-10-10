using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

/// <summary>
///     The client surface over one or more mapped drive blocks. Opening picks a producer per
///     drive, warm-starts from a valid block or cold-scans, and publishes a snapshot. A rescan
///     writes a new block beside the old one and swaps it in; handles minted from the old
///     snapshot keep the old block mapped until they are collected, so a held handle never
///     dangles across a rescan. Split into partial files by responsibility: this file is
///     construction and the read surface, <c>FileIndex.Scanning.cs</c> is the cold-scan and
///     warm-start path used when opening a drive, and <c>FileIndex.Rescan.cs</c> is rescanning
///     and snapshot publication.
/// </summary>
public sealed partial class FileIndex : IAsyncDisposable
{
    readonly List<DriveBlock> _driveBlocks = [];
    readonly Dictionary<char, IndexedDrive> _driveConfigurations = [];
    readonly List<DriveStatus> _blocklessDriveStatuses = [];
    readonly Dictionary<ushort, int> _accessDeniedSubtreeCountByOrdinal = [];
    readonly Dictionary<ushort, int> _skippedRecordCountByOrdinal = [];
    readonly Dictionary<ushort, string> _mftProducerFailureMessagesByOrdinal = [];
    readonly Dictionary<ushort, string> _watchFailureMessagesByOrdinal = [];
    readonly Dictionary<ushort, BlockSource> _blockSourcesByOrdinal = [];
    readonly Dictionary<ushort, CacheSlotState> _cacheSlotsByOrdinal = [];

    /// <summary>
    ///     One entry per drive whose cached block was rejected because its journal checkpoint
    ///     could no longer be resumed. It outlives the cold scan that follows, so a consumer
    ///     can explain the rescan once the index is open.
    /// </summary>
    readonly Dictionary<ushort, JournalCheckpointLoss> _checkpointLossesByOrdinal = [];

    readonly Dictionary<char, BlockOwnerLock> _canonicalLocksByLetter = [];
    readonly List<SnapshotRelease> _retiredSnapshots = [];
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
    ///     Guards every drive's watch records and every ordinal-keyed record, and is the one step
    ///     that publishes a snapshot: a commit of a drive's block, the new
    ///     <see cref="Snapshot.Create" />, and the retirement of the previous snapshot all happen
    ///     under it, so a reader (<see cref="Drives" />, <see cref="CurrentSnapshot" />,
    ///     <see cref="TryGetDriveOrdinal" />) never observes a partial swap. It is innermost in the
    ///     lock order (see <see cref="DriveRuntime" />) and never held across an <c>await</c>.
    /// </summary>
    readonly Lock _stateLock = new();

    Snapshot? _snapshot;
    bool _disposed;

    /// <summary>
    ///     The open's settle counts by drive letter, claimed under <see cref="_stateLock" /> in the
    ///     same section that records the drive's final state, so count order is settle order and,
    ///     for drives with a block, ordinal order.
    /// </summary>
    readonly Dictionary<char, int> _settledCountsByLetter = [];

    /// <summary>
    ///     Claims the next settle count for a drive whose final open state is being recorded. The
    ///     caller holds <see cref="_stateLock" />.
    /// </summary>
    void ClaimSettledCountLocked(char driveLetter) =>
        _settledCountsByLetter[char.ToUpperInvariant(driveLetter)] = _settledCountsByLetter.Count + 1;

    FileIndex(FileIndexOptions options, string cacheDirectoryPath)
    {
        // A private copy: the index re-enumerates its drives on every status read, so a later
        // change to the caller's list must never reach it.
        _options = options with { Drives = [.. options.Drives] };
        CacheDirectoryPath = cacheDirectoryPath;
        _enumerateCacheFiles = options.EnumerateCacheFilesForTest ?? Directory.EnumerateFiles;
        _snapshot = Snapshot.Create([]);
        foreach (var drive in _options.Drives)
        {
            var driveLetter = char.ToUpperInvariant(drive.DriveLetter);
            _driveRuntimes.TryAdd(driveLetter, new DriveRuntime(driveLetter));
        }
    }

    /// <summary>
    ///     The explicitly configured cache directory, or empty for NoCache and dump opens.
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
            DrivesReadBeforeLockForTest?.Invoke();
            lock (_stateLock)
            {
                // Checked under the lock: disposal sets the flag under it before it unpublishes
                // any block, so a read that sees the flag clear sees every block still published.
                ObjectDisposedException.ThrowIf(_disposed, this);
                var statuses = new List<DriveStatus>(_options.Drives.Count);
                foreach (var configured in _options.Drives)
                {
                    var driveLetter = char.ToUpperInvariant(configured.DriveLetter);
                    statuses.Add(DescribeOnlineDrive(driveLetter) ?? DescribeBlocklessDrive(driveLetter));
                }

                return statuses;
            }
        }
    }

    /// <summary>A test seam: invoked by a read of <see cref="Drives" /> just before it takes <see cref="_stateLock" />.</summary>
    internal Action? DrivesReadBeforeLockForTest { get; set; }

    internal Snapshot CurrentSnapshot
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
    ///     missing drive settles as <see cref="DriveState.Offline" />, and an MFT-producer scan
    ///     failure settles as <see cref="DriveState.Failed" /> in <see cref="Drives" /> without
    ///     failing the open; an enumeration production failure throws.
    /// </summary>
    /// <param name="options">The drives to index and the cache, progress and producer settings.</param>
    /// <param name="cancellationToken">
    ///     Cancels the open. Every drive's settle finishes first, and blocks already adopted are
    ///     released before the cancellation is thrown.
    /// </param>
    /// <returns>The opened index, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    /// <exception cref="ArgumentException">Cache configuration is missing or dump options are invalid.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the open finished.</exception>
    public static async Task<FileIndex> OpenAsync(FileIndexOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string cacheDirectoryPath;
        if (options.MftSource?.DumpIdentity is { } dumpIdentity)
        {
            // A dump block is never cached, so validation runs first and no cache path is resolved
            // or created.
            dumpIdentity.ValidateOptions(options);
            cacheDirectoryPath = string.Empty;
        }
        else if (options.NoCache)
        {
            cacheDirectoryPath = string.Empty;
        }
        else
        {
            var suppliedCacheDirectory = options.CacheDirectory;
            if (string.IsNullOrWhiteSpace(suppliedCacheDirectory))
            {
                throw new ArgumentException("Set FileIndexOptions.CacheDirectory or enable NoCache.", nameof(options));
            }

            cacheDirectoryPath = suppliedCacheDirectory;
            CacheDirectory.EnsureCreated(cacheDirectoryPath);
        }

        var index = new FileIndex(options, cacheDirectoryPath);
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

    internal bool TryGetDriveOrdinal(char driveLetter, out ushort driveOrdinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            return TryGetDriveOrdinalLocked(driveLetter, out driveOrdinal);
        }
    }

    /// <summary>
    ///     The same lookup without the disposal check, for the watch paths that run while an index
    ///     is being torn down: recording a drive's failure or clearing it on an index that is going
    ///     away has nothing to do, not a different exception to raise over the one already in
    ///     flight. The caller holds <see cref="_stateLock" />.
    /// </summary>
    bool TryGetDriveOrdinalLocked(char driveLetter, out ushort driveOrdinal)
    {
        foreach (var driveBlock in _driveBlocks)
        {
            if (char.ToUpperInvariant(driveBlock.DriveLetter) == char.ToUpperInvariant(driveLetter))
            {
                driveOrdinal = driveBlock.DriveOrdinal;
                return true;
            }
        }

        driveOrdinal = 0;
        return false;
    }

    /// <summary>
    ///     Prevents new index operations and cancels every rescan in flight, stops every drive's
    ///     live watch and waits for each drive's teardown, takes every drive's lifecycle gate and
    ///     then every drive's write gate in ascending drive-letter order, so no rescan, start,
    ///     commit, or batch is still running, and releases every snapshot it holds, current and
    ///     retired. A fault a drive's watch ended with is never thrown from here: it was announced
    ///     through <see cref="WatchFaulted" /> when it happened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Disposing while another thread is running a scan is safe, and is what this method
    ///         being asynchronous buys. Six entry points scan rows, and each holds a borrow on
    ///         the snapshot it reads for its whole duration: <see cref="Find" />,
    ///         <see cref="Search" />, <see cref="Enumerate" />, <see cref="EnumerateRows" />
    ///         and <see cref="Root" /> on this class, and
    ///         <see cref="FileEntry.Children" /> on a handle. Disposal waits for every one of
    ///         those borrows, on the current snapshot and on the retired ones, before it unmaps
    ///         anything.
    ///     </para>
    ///     <para>
    ///         The five queries on this class also observe a token linked to the index's disposal,
    ///         so disposal cancels them rather than waiting them out: each ends with
    ///         <see cref="OperationCanceledException" /> or, if it had not started,
    ///         <see cref="ObjectDisposedException" />, and the wait is as long as they take to
    ///         reach their next checkpoint, at most 4096 rows of active scanning. A suspended
    ///         <see cref="Enumerate" /> enumerator holds its borrow between yields: disposal waits
    ///         until it advances or is disposed, or, if abandoned, until garbage collection and
    ///         finalization return its borrow. Dispose enumerators promptly rather than relying on
    ///         collection, whose timing is not guaranteed. <see cref="FileEntry.Children" />
    ///         is the exception: a handle carries no reference to its index, so there is no
    ///         disposal token to link and this method waits that listing out instead, which is one
    ///         pass over the drive's rows unless its caller passes a token of its own.
    ///     </para>
    ///     <para>
    ///         Every other member of <see cref="FileEntry" /> reads a single row rather than
    ///         scanning, and carries no borrow. A read through a handle that is ordered after the
    ///         disposal throws <see cref="ObjectDisposedException" /> from the per-access check
    ///         <see cref="FileEntry.IsDisposed" /> describes.
    ///     </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (RejectInsideHandler(nameof(DisposeAsync)) is { } rejection)
        {
            throw rejection;
        }

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        DisposedFlagSetForTest?.Invoke();

        // Before the gates, not after: a query already inside the index holds a borrow that every
        // release below waits for, so it has to be told to stop before anything starts waiting on
        // it. A query that has not started yet is turned away by the disposed flag instead. The
        // same token cancels every drive's watch start and every rescan still in progress.
        ExceptionDispatchInfo? cancellationFailure = null;
        try
        {
            await _disposalCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            // A callback on this token belongs to someone else, and a throw from one is not a
            // reason to abandon the mappings: the disposed flag is already set, so the early
            // return above means no later call would ever finish the job. The release runs, and
            // the failure is raised after it, with its original stack, or alongside the release's
            // own failure if the release fails too.
            cancellationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        // No token of its own, and none may abandon a pump whose blocks this is about to unmap.
        await StopEveryWatchForDisposalAsync().ConfigureAwait(false);

        try
        {
            await ReleaseSnapshotsForDisposalAsync(cancellationFailure).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                foreach (var ownerLock in _canonicalLocksByLetter.Values)
                {
                    ownerLock.Dispose();
                }

                _canonicalLocksByLetter.Clear();
            }
        }

        cancellationFailure?.Throw();
    }

    /// <summary>
    ///     Unwinds every block already added to <see cref="_driveBlocks" /> when opening fails or
    ///     is cancelled partway through <see cref="FileIndexOptions.Drives" />. These blocks were
    ///     never handed to <see cref="Snapshot.Create" />, so their reference count is still
    ///     zero and <see cref="DriveBlock.Release" /> would throw; disposing the underlying
    ///     <see cref="BlockFile" /> directly is the correct unwind instead. A warm-started block's
    ///     file is left on disk (still valid for the next open); a freshly cold-scanned block's
    ///     file is left on disk too when cached, or deleted immediately when it was created with
    ///     delete-on-close for no-cache mode.
    /// </summary>
    void ReleaseUnpublishedBlocks()
    {
        lock (_stateLock)
        {
            foreach (var driveBlock in _driveBlocks)
            {
                driveBlock.Block.Dispose();
            }

            _driveBlocks.Clear();
            foreach (var ownerLock in _canonicalLocksByLetter.Values)
            {
                ownerLock.Dispose();
            }

            _canonicalLocksByLetter.Clear();
        }
    }

    DriveStatus? DescribeOnlineDrive(char driveLetter)
    {
        foreach (var driveBlock in _driveBlocks)
        {
            if (char.ToUpperInvariant(driveBlock.DriveLetter) == driveLetter)
            {
                return DescribeOnlineDriveBlock(driveBlock);
            }
        }

        return null;
    }

    DriveStatus DescribeOnlineDriveBlock(DriveBlock driveBlock)
    {
        var runtime = GetDriveRuntime(driveBlock.DriveLetter);
        ref readonly var header = ref driveBlock.Block.Header;
        return new DriveStatus(
            driveBlock.DriveLetter,
            header.IsCompactionNeeded ? DriveState.Stale : DriveState.Ready)
        {
            FailureMessage = _mftProducerFailureMessagesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
            Block = new DriveBlockStatus
            {
                Source = _blockSourcesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
                CacheSlot = _cacheSlotsByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
                LiveRowCount = header.LiveRowCount,
                ScanTimestamp = header.ScanTimestampUtc,
                CompactionNeeded = header.IsCompactionNeeded,
                SkippedRecordCount = _skippedRecordCountByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
                AccessDeniedSubtreeCount = _accessDeniedSubtreeCountByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal)
            },
            Watch = new DriveWatchStatus
            {
                Supported = driveBlock.ProducerKind == ProducerKind.Mft && WatchSourceOrNull is not null,
                Requested = runtime.WatchRequested,
                CatchUpState = GetWatchCatchUpStateLocked(driveBlock.DriveLetter),
                StateVersion = runtime.WatchStateVersion,
                FailureMessage = _watchFailureMessagesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
                ConsecutiveLostCatchUps = runtime.ConsecutiveLostCatchUps,
                RecoveryStopped = runtime.RecoveryStopped,
                CheckpointLoss = _checkpointLossesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal)
            }
        };
    }

    DriveStatus DescribeBlocklessDrive(char driveLetter)
    {
        // Every configured drive of an undisposed index is online or blockless once it settles.
        return _blocklessDriveStatuses.First(status => char.ToUpperInvariant(status.DriveLetter) == driveLetter);
    }
}
