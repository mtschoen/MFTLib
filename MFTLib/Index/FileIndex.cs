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
    readonly List<DriveStatus> _offlineDrives = [];
    readonly Dictionary<ushort, BlockValidationResult> _discardedBlocksByOrdinal = [];
    readonly Dictionary<ushort, int> _accessDeniedSubtreeCountByOrdinal = [];
    readonly List<WeakReference<Snapshot>> _retiredSnapshots = [];
    readonly FileIndexOptions _options;
    readonly SemaphoreSlim _swapGate = new(1, 1);

    /// <summary>
    ///     Guards reads and writes of <see cref="_snapshot" /> and <see cref="_driveBlocks" />
    ///     against a concurrent reader (<see cref="Drives" />, <see cref="CurrentSnapshot" />,
    ///     <see cref="TryGetDriveOrdinal" />) observing a partial swap. <see cref="_swapGate" />
    ///     already serializes the mutations themselves against each other; this is only about
    ///     what a reader on another thread can see mid-mutation, so it is never held across an
    ///     <c>await</c>.
    /// </summary>
    readonly Lock _stateLock = new();

    Snapshot _snapshot;
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
    ///     Ordered to follow <see cref="FileIndexOptions.Drives" />, online or offline.
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
                    statuses.Add(DescribeOnlineDrive(driveLetter) ?? DescribeOfflineDrive(driveLetter));
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
                return _snapshot;
            }
        }
    }

    public static async Task<FileIndex> OpenAsync(FileIndexOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ProducerPolicy == ProducerPolicy.MftOnly)
        {
            throw new NotSupportedException(
                "The MFT producer is not available in this build; use ProducerPolicy.Auto or EnumerationOnly.");
        }

        var cacheDirectoryPath = options.CacheDirectory ?? CacheDirectory.ResolveDefaultPath();
        CacheDirectory.EnsureCreated(cacheDirectoryPath);

        var index = new FileIndex(options, cacheDirectoryPath);
        try
        {
            foreach (var drive in options.Drives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await index.AddDriveAsync(drive, cancellationToken).ConfigureAwait(false);
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
            foreach (var driveBlock in _driveBlocks)
            {
                if (char.ToUpperInvariant(driveBlock.DriveLetter) == char.ToUpperInvariant(driveLetter))
                {
                    driveOrdinal = driveBlock.DriveOrdinal;
                    return true;
                }
            }
        }

        driveOrdinal = 0;
        return false;
    }

    /// <summary>
    ///     Releases every block this index holds, current and retired, and unmaps their views. It
    ///     does not wait for outstanding work: disposal must not overlap an in-flight query, and
    ///     every <see cref="FileEntry" /> minted from this index is invalid once it returns. A
    ///     query already inside a column scan holds a span over memory this call unmaps, and
    ///     touching it afterwards faults the process rather than raising a catchable exception.
    ///     Let every query and every handle go before disposing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _swapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ReleaseAllRetiredSnapshots();

            lock (_stateLock)
            {
                _snapshot.ReleaseNow();
                _driveBlocks.Clear();
            }
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
            if (char.ToUpperInvariant(driveBlock.DriveLetter) != driveLetter)
            {
                continue;
            }

            var discardedBlock = _discardedBlocksByOrdinal.TryGetValue(driveBlock.DriveOrdinal, out var reason)
                ? reason
                : (BlockValidationResult?)null;
            var accessDeniedSubtreeCount =
                _accessDeniedSubtreeCountByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal);
            return DescribeDrive(driveBlock, discardedBlock, accessDeniedSubtreeCount);
        }

        return null;
    }

    DriveStatus DescribeOfflineDrive(char driveLetter)
    {
        foreach (var offline in _offlineDrives)
        {
            if (char.ToUpperInvariant(offline.DriveLetter) == driveLetter)
            {
                return offline;
            }
        }

        throw new InvalidOperationException(
            $"Drive {driveLetter} is in FileIndexOptions.Drives but was never added as online or offline.");
    }

    static DriveStatus DescribeDrive(DriveBlock driveBlock, BlockValidationResult? discardedBlock,
        int accessDeniedSubtreeCount)
    {
        ref readonly var header = ref driveBlock.Block.Header;
        return new DriveStatus
        {
            DriveLetter = driveBlock.DriveLetter,
            ProducerKind = driveBlock.ProducerKind,
            State = header.IsCompactionNeeded ? DriveState.Stale : DriveState.Ready,
            RowCount = header.RowCount,
            ScanTimestamp = header.ScanTimestampUtc,
            CompactionNeeded = header.IsCompactionNeeded,
            WatchSupported = driveBlock.ProducerKind == ProducerKind.Mft,
            AccessDeniedSubtreeCount = accessDeniedSubtreeCount,
            DiscardedBlock = discardedBlock
        };
    }
}
