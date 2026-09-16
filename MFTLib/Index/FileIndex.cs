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
    readonly Dictionary<ushort, BlockValidationResult> _discardedBlocksByOrdinal = [];
    readonly Dictionary<ushort, int> _accessDeniedSubtreeCountByOrdinal = [];
    readonly Dictionary<ushort, string> _mftProducerFailureMessagesByOrdinal = [];
    readonly Dictionary<ushort, string> _watchFailureMessagesByOrdinal = [];
    readonly Dictionary<ushort, BlockSource> _blockSourcesByOrdinal = [];
    readonly List<RetiredSnapshot> _retiredSnapshots = [];
    readonly FileIndexOptions _options;
    readonly SemaphoreSlim _rescanGate = new(1, 1);
    readonly SemaphoreSlim _swapGate = new(1, 1);

    /// <summary>
    ///     Cancelled by <see cref="DisposeAsync" /> before it waits for anything. Every query's
    ///     effective token is linked to this one, so a scan in flight is told to stop rather than
    ///     holding disposal open for the rest of a whole-drive pass. Deliberately not disposed,
    ///     for the same reason as <see cref="_swapGate" />: a query that raced the disposal still
    ///     reads this token, and a disposed source would answer it with an exception naming the
    ///     source rather than the index.
    /// </summary>
    readonly CancellationTokenSource _disposalCancellation = new();

    /// <summary>
    ///     The signal that stops the queries in flight when this index is disposed. A query's
    ///     effective token is this one, or this one linked with the caller's own.
    /// </summary>
    internal CancellationToken DisposalToken => _disposalCancellation.Token;

    sealed class RetiredSnapshot
    {
        internal RetiredSnapshot(Snapshot snapshot)
        {
            Release = snapshot.ReleaseState;
        }

        internal SnapshotRelease Release { get; }
    }

    /// <summary>
    ///     Guards reads and writes of <see cref="_snapshot" /> and <see cref="_driveBlocks" />
    ///     against a concurrent reader (<see cref="Drives" />, <see cref="CurrentSnapshot" />,
    ///     <see cref="TryGetDriveOrdinal" />) observing a partial swap. <see cref="_swapGate" />
    ///     already serializes block mutations; this also serializes watch initialization and guards
    ///     what a reader on another thread can see mid-mutation. It is never held across an
    ///     <c>await</c>.
    /// </summary>
    readonly Lock _stateLock = new();

    Snapshot? _snapshot;
    WatchSession? _watchSession;
    bool _disposed;

    FileIndex(FileIndexOptions options, string cacheDirectoryPath)
    {
        _options = options;
        CacheDirectoryPath = cacheDirectoryPath;
        _snapshot = Snapshot.Create([]);
    }

    public string CacheDirectoryPath { get; }

    /// <summary>
    ///     Recomputed from the block headers on every read, so it reflects the latest mutation.
    ///     Ordered to follow <see cref="FileIndexOptions.Drives" />, including blockless drives.
    /// </summary>
    public IReadOnlyList<DriveStatus> Drives
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateLock)
            {
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

    public static async Task<FileIndex> OpenAsync(FileIndexOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cacheDirectoryPath = options.CacheDirectory ?? CacheDirectory.ResolveDefaultPath();
        CacheDirectory.EnsureCreated(cacheDirectoryPath);

        var index = new FileIndex(options, cacheDirectoryPath);
        try
        {
            var openProgress = options.OpenProgress;
            var openOrdinal = 0;
            foreach (var drive in options.Drives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (openProgress is null)
                {
                    await index.AddDriveAsync(drive, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await index.AddDriveWithProgressAsync(drive, openOrdinal + 1, options.Drives.Count, openProgress,
                        cancellationToken).ConfigureAwait(false);
                }

                openOrdinal++;
            }
        }
        catch
        {
            // A later drive's failure or a mid-loop cancellation must not leak the mappings
            // (and, for a no-cache block, the temp file) that earlier drives already opened.
            index.ReleaseUnpublishedBlocks();
            throw;
        }

        index.PublishSnapshot();
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
    ///     Stops the live watch, prevents new index operations, waits for mutation and rescan
    ///     ownership of <see cref="_swapGate" />, and releases every snapshot it holds, current
    ///     and retired.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Disposing while another thread is running a scan is safe, and is what this method
    ///         being asynchronous buys. Eight entry points scan rows, and each holds a borrow on
    ///         the snapshot it reads for its whole duration: <see cref="Find" />,
    ///         <see cref="FindByName" />, <see cref="Search" />, <see cref="Enumerate" />, <see cref="Largest" />,
    ///         <see cref="DuplicateNames" /> and <see cref="Root" /> on this class, and
    ///         <see cref="FileEntry.Children" /> on a handle. Disposal waits for every one of
    ///         those borrows, on the current snapshot and on the retired ones, before it unmaps
    ///         anything.
    ///     </para>
    ///     <para>
    ///         The seven queries on this class also observe a token linked to the index's disposal,
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
        if (_disposed)
        {
            return;
        }

        try
        {
            // No token of its own, and none may abandon a pump whose blocks this is about to unmap.
            await StopWatchingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Disposal still owns the block mappings after a reported pump fault. The fault was
            // announced when observed and StopWatchingAsync remains the explicit rethrow surface.
            // Discarded through the variable rather than an empty body, which is this repository's
            // idiom for a deliberate swallow and what keeps RCS1075 honest here.
            _ = exception;
        }

        _disposed = true;

        // Before the gate, not after: a query already inside the index holds a borrow that every
        // release below waits for, so it has to be told to stop before anything starts waiting on
        // it. A query that has not started yet is turned away by the disposed flag instead.
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

        await _rescanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _swapGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ReleaseAllRetiredSnapshotsAsync().ConfigureAwait(false);

                Snapshot? current;
                lock (_stateLock)
                {
                    current = _snapshot;
                    _snapshot = null;
                    _driveBlocks.Clear();
                    _retiredSnapshots.Clear();
                }

                // Unconditional: a consumer that disposed everything it owns has asked for the
                // mappings to go, and a handle it kept is answered by ObjectDisposedException
                // rather than by an indefinitely open block file. See FileEntry.IsDisposed.
                // Released outside _stateLock because this waits out both a release another caller
                // already started and every query still reading the snapshot, and a reader must not
                // be shut out of the lock for that long.
                if (current is not null)
                {
                    await current.ReleaseNowAsync().ConfigureAwait(false);
                }
            }
            catch (Exception releaseFailure) when (cancellationFailure is not null)
            {
                // Both halves failed. The captured cancellation failure has nowhere left to go once
                // this one is in flight, and a consumer told the release failed would never learn
                // that its own callback threw first, so both come out together, in the order they
                // happened. The first is itself the AggregateException CancelAsync raised, so a
                // consumer that wants the leaves calls Flatten().
                throw new AggregateException(cancellationFailure.SourceException, releaseFailure);
            }
            finally
            {
                // The gate is released but deliberately not disposed. A waiter admitted a moment
                // after the flag was set throws ObjectDisposedException from its own re-check, which
                // is a caller's error to handle; disposing the gate would instead throw that from the
                // waiter's finally as it released, hiding the first exception. This SemaphoreSlim
                // never had its AvailableWaitHandle taken, so it holds nothing that needs a
                // deterministic release.
                _swapGate.Release();
            }
        }
        finally
        {
            _rescanGate.Release();
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
        var discardedBlock = _discardedBlocksByOrdinal.TryGetValue(driveBlock.DriveOrdinal, out var reason)
            ? reason
            : (BlockValidationResult?)null;
        var annotations = new DriveStatusAnnotations(
            discardedBlock,
            _accessDeniedSubtreeCountByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
            _mftProducerFailureMessagesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
            _watchFailureMessagesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal),
            _blockSourcesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal));
        return DescribeDrive(driveBlock, in annotations);
    }

    DriveStatus DescribeSettledDrive(char driveLetter)
    {
        lock (_stateLock)
        {
            if (_driveBlocks.Count > 0 && char.ToUpperInvariant(_driveBlocks[^1].DriveLetter) == driveLetter)
            {
                return DescribeOnlineDriveBlock(_driveBlocks[^1]);
            }

            if (_blocklessDriveStatuses.Count > 0 &&
                char.ToUpperInvariant(_blocklessDriveStatuses[^1].DriveLetter) == driveLetter)
            {
                return _blocklessDriveStatuses[^1];
            }

            return DescribeOnlineDrive(driveLetter) ?? DescribeBlocklessDrive(driveLetter);
        }
    }

    DriveStatus DescribeBlocklessDrive(char driveLetter)
    {
        foreach (var status in _blocklessDriveStatuses)
        {
            if (char.ToUpperInvariant(status.DriveLetter) == driveLetter)
            {
                return status;
            }
        }

        throw new InvalidOperationException(
            $"Drive {driveLetter} is in FileIndexOptions.Drives but has no online or blockless status.");
    }

    /// <summary>Everything a drive's status carries that is not read off its block header.</summary>
    readonly record struct DriveStatusAnnotations(
        BlockValidationResult? DiscardedBlock,
        int AccessDeniedSubtreeCount,
        string? MftProducerFailureMessage,
        string? WatchFailureMessage,
        BlockSource BlockSource);

    static DriveStatus DescribeDrive(DriveBlock driveBlock, in DriveStatusAnnotations annotations)
    {
        ref readonly var header = ref driveBlock.Block.Header;
        return new DriveStatus
        {
            DriveLetter = driveBlock.DriveLetter,
            ProducerKind = driveBlock.ProducerKind,
            BlockSource = annotations.BlockSource,
            State = header.IsCompactionNeeded ? DriveState.Stale : DriveState.Ready,
            RowCount = header.RowCount,
            LiveRowCount = header.LiveRowCount,
            ScanTimestamp = header.ScanTimestampUtc,
            CompactionNeeded = header.IsCompactionNeeded,
            WatchSupported = driveBlock.ProducerKind == ProducerKind.Mft,
            AccessDeniedSubtreeCount = annotations.AccessDeniedSubtreeCount,
            DiscardedBlock = annotations.DiscardedBlock,
            MftProducerFailureMessage = annotations.MftProducerFailureMessage,
            WatchFailureMessage = annotations.WatchFailureMessage
        };
    }
}
