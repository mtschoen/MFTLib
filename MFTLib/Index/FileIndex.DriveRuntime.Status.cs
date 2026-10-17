using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        public DriveStatus DescribeLocked()
        {
            if (Block is not { } driveBlock)
            {
                return new DriveStatus(Drive, BlocklessState)
                {
                    Failure = Failure,
                    Watch = new DriveWatchStatus { CheckpointLoss = CheckpointLoss }
                };
            }

            var result = LastResult ?? throw new InvalidOperationException(
                $"Drive {Drive} has a block without its adopted result.");
            ref readonly var header = ref driveBlock.Block.Header;
            return new DriveStatus(Drive, DriveState.Ready)
            {
                Failure = Failure,
                Block = new DriveBlockStatus
                {
                    Origin = result.Origin,
                    CacheLocation = result.CacheLocation,
                    LiveRowCount = header.LiveRowCount,
                    ScanTimestamp = header.ScanTimestampUtc,
                    IsCapacityExhausted = header.IsCapacityExhausted,
                    SkippedRecordCount = result.SkippedRecordCount,
                    AccessDeniedSubtreeCount = result.AccessDeniedSubtreeCount
                },
                Watch = DescribeWatchLocked()
            };
        }

        /// <summary>
        ///     The drive's <see cref="DriveWatchStatus.State" />, from its watch records:
        ///     <see cref="WatchState.Recovering" /> while a recovery of the drive is queued or
        ///     running or a scan of the watched drive retries after a lost catch-up, the current
        ///     instance's slot while it runs,
        ///     <see cref="WatchState.Faulted" /> for a faulted instance or a failed or refused
        ///     start, <see cref="WatchState.CatchingUp" /> while the watch is requested and its
        ///     replacement has not registered yet, and <see cref="WatchState.NotStarted" />
        ///     otherwise. The caller holds <see cref="FileIndex._stateLock" />.
        /// </summary>
        public WatchState GetWatchStateLocked()
        {
            if (RetryingLostCatchUp || RecoveryState == RecoveryState.Recovering)
            {
                return WatchState.Recovering;
            }

            if (Current is { } instance)
            {
                return instance.State == WatchInstanceState.Faulted
                    ? WatchState.Faulted
                    : instance.CatchUp.State;
            }

            if (RefusedStartFault is not null)
            {
                return WatchState.Faulted;
            }

            return WatchRequested ? WatchState.CatchingUp : WatchState.NotStarted;
        }

        /// <summary>
        ///     The one builder of a drive's <see cref="DriveWatchStatus" />, shared by
        ///     <see cref="Drives" /> and the changes <see cref="FileIndex.WatchStateChanged" /> delivers. A change is
        ///     only ever noted for a drive with a block: a watch is requested only against a block,
        ///     and blocks are never removed. The caller holds <see cref="FileIndex._stateLock" />.
        /// </summary>
        public DriveWatchStatus DescribeWatchLocked()
        {
            var driveBlock = Block ?? throw new InvalidOperationException($"Drive {Drive} has no block.");
            return new DriveWatchStatus
            {
                IsSupported = driveBlock.Kind.CanWatch && WatchSourceOrNull is not null,
                IsRequested = WatchRequested,
                State = GetWatchStateLocked(),
                StateVersion = WatchStateVersion,
                FailureMessage = WatchFailureMessage,
                ConsecutiveLostCatchUps = ConsecutiveLostCatchUps,
                IsRecoveryStopped = RecoveryStopped,
                CheckpointLoss = CheckpointLoss
            };
        }

        public bool HasWatchableBlockLocked() => WatchSourceOrNull is not null && Block is { Kind.CanWatch: true };

        public bool IsWatchingLocked() =>
            WatchRequested || Current is not null || Retiring is not null;

        [SupportedOSPlatform("windows")]
        public UsnJournalSettings QueryUsnJournalSettings()
        {
            if (!DriveKind.For(Configuration.Producer, Options.MftSource).HasJournalSettings)
            {
                throw new InvalidOperationException(
                    MftIndexSource.FormatUnavailable(Drive, MftIndexSource.NoJournalSettingsReason));
            }

            return UsnJournalSettingsQuery.Query(Drive);
        }
    }
}
