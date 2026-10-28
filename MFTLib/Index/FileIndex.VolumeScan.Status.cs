namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        public RootStatus DescribeLocked(IndexView view)
        {
            var resolved = view.Resolved;
            var root = view.Root;
            var unavailable = resolved.MftUnavailable ?? _lastMftFailure;
            if (view.Block is not { } rootBlock)
            {
                // Offline here is a root absent at open, which never reached selection.
                var reachedSelection = BlocklessState != RootState.Offline;
                return new RootStatus(root, BlocklessState)
                {
                    Failure = _failure,
                    Watch = new RootWatchStatus { CheckpointLoss = _checkpointLoss },
                    Producer = reachedSelection ? resolved.Producer : null,
                    MftUnavailable = reachedSelection ? unavailable : null
                };
            }

            if (!rootBlock.View.IsPresent)
            {
                // A directory view whose directory is out of the block (renamed away, or not yet created) reads as
                // offline with no block: the volume block stays mapped and watched, and a later batch can bring it back.
                return new RootStatus(root, RootState.Offline)
                {
                    Watch = DescribeWatchLocked(view),
                    Producer = resolved.Producer,
                    MftUnavailable = unavailable
                };
            }

            var result = _lastResult ?? throw new InvalidOperationException(
                $"Root {Root} has a block without its adopted result.");
            ref readonly var header = ref rootBlock.Block.Header;
            return new RootStatus(root, RootState.Ready)
            {
                Failure = _failure,
                Producer = resolved.Producer,
                MftUnavailable = unavailable,
                Block = new RootBlockStatus
                {
                    Origin = result.Origin,
                    CacheLocation = result.CacheLocation,
                    LiveRowCount = header.LiveRowCount,
                    ScanTimestamp = header.ScanTimestampUtc,
                    Generation = rootBlock.Block.Generation,
                    IsCapacityExhausted = header.IsCapacityExhausted,
                    SkippedRecordCount = result.SkippedRecordCount,
                    AccessDeniedSubtreeCount = (int)Math.Min(header.AccessDeniedSubtreeCount, int.MaxValue)
                },
                Watch = DescribeWatchLocked(view)
            };
        }

        /// <summary>
        ///     The root's <see cref="RootWatchStatus.State" />, from its watch records:
        ///     <see cref="WatchState.Recovering" /> while a recovery of the root is queued or
        ///     running or a scan of the watched root retries after a lost catch-up, the current
        ///     instance's slot while it runs,
        ///     <see cref="WatchState.Faulted" /> for a faulted instance or a failed or refused
        ///     start, <see cref="WatchState.CatchingUp" /> while the watch is requested and its
        ///     replacement has not registered yet, and <see cref="WatchState.NotStarted" />
        ///     otherwise. The caller holds <see cref="FileIndex.StateLock" />.
        /// </summary>
        WatchState GetWatchStateLocked()
        {
            if (_retryingLostCatchUp || _recoveryState == RecoveryState.Recovering)
            {
                return WatchState.Recovering;
            }

            if (Current is { } instance)
            {
                return instance.State == WatchInstanceState.Faulted
                    ? WatchState.Faulted
                    : instance.CatchUp.State;
            }

            if (_refusedStartFault is not null)
            {
                return WatchState.Faulted;
            }

            return _watchRequested ? WatchState.CatchingUp : WatchState.NotStarted;
        }

        /// <summary>
        ///     The one builder of a root's <see cref="RootWatchStatus" />, shared by
        ///     <see cref="Status" /> and the changes <see cref="FileIndex.WatchStateChanged" /> delivers. A change is
        ///     only ever noted for a root with a block: a watch is requested only against a block,
        ///     and blocks are never removed. The caller holds <see cref="FileIndex.StateLock" />.
        /// </summary>
        RootWatchStatus DescribeWatchLocked(IndexView view)
        {
            return new RootWatchStatus
            {
                IsSupported = view.Kind.CanWatch && WatchSourceOrNull is not null,
                IsRequested = _watchRequested,
                State = GetWatchStateLocked(),
                StateVersion = _watchStateVersion,
                FailureMessage = _watchFailureMessage,
                ConsecutiveLostCatchUps = _consecutiveLostCatchUps,
                IsRecoveryStopped = _recoveryStopped,
                CheckpointLoss = _checkpointLoss
            };
        }

        public bool HasWatchableBlockLocked() => WatchSourceOrNull is not null && Block is { Kind.CanWatch: true };

        public bool IsWatchingLocked() =>
            _watchRequested || Current is not null || _retiring is not null;

        public UsnJournalSettings QueryUsnJournalSettings()
        {
            if (Resolved.IsDump)
            {
                throw new InvalidOperationException(
                    MftIndexSource.FormatUnavailable(Volume, MftIndexSource.NoJournalSettingsReason));
            }

            if (Resolved is not { Producer: ProducerKind.Mft, Volume: { } volume })
            {
                throw new InvalidOperationException(
                    $"Root {Root} is walked rather than read from the MFT, so it has no USN journal settings.");
            }

            return UsnJournalSettingsQuery.Query(volume);
        }
    }
}
