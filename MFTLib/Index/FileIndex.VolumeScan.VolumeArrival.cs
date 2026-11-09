using System.ComponentModel;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     A recovery whose last retry failed while its volume still read not ready: the faulted
        ///     instance and the block it owned, which a volume arrival revalidates before it revives
        ///     the recovery.
        /// </summary>
        sealed record AbsentRecovery(WatchInstance FailedInstance, RootBlock FailedBlock);

        /// <summary>
        ///     The root's recovery that gave up on an absent volume, until an arrival revives it or a
        ///     stop, start, rescan or disposal clears it. Guarded by <see cref="StateLock" />.
        /// </summary>
        AbsentRecovery? _absentRecovery;

        /// <summary>
        ///     The scan's subscription to volume arrivals, held while a recovery is queued or running or
        ///     a recovery gave up on an absent volume. Guarded by <see cref="StateLock" />.
        /// </summary>
        IDisposable? _arrivalSubscription;

        bool NeedsVolumeArrivalsLocked => !IsClosing && (Recovery is not null || _absentRecovery is not null);

        /// <summary>
        ///     Subscribes to volume arrivals for the running recovery, outside the state lock since the
        ///     platform registration is a system call. A failed registration is reported to
        ///     <see cref="FileIndexOptions.Diagnostics" />, and the recovery then retries on its schedule
        ///     only.
        /// </summary>
        void SubscribeToVolumeArrivals()
        {
            lock (StateLock)
            {
                if (_arrivalSubscription is not null || !NeedsVolumeArrivalsLocked)
                {
                    return;
                }
            }

            IDisposable subscription;
            try
            {
                subscription = Options.VolumeArrivals.Subscribe(OnVolumeArrival);
            }
            catch (Win32Exception failure)
            {
                DiagnosticsReport.Send(Options.Diagnostics,
                    $"Root {Root}: volume arrival notifications are unavailable ({failure.Message}); automatic recovery retries on its schedule only.");
                return;
            }

            lock (StateLock)
            {
                if (_arrivalSubscription is null && NeedsVolumeArrivalsLocked)
                {
                    _arrivalSubscription = subscription;
                    return;
                }
            }

            subscription.Dispose();
        }

        /// <summary>
        ///     Drops the arrival subscription once nothing needs it. Safe under the state lock: the
        ///     platform callback only queues arrivals, so unregistering never waits on this lock.
        /// </summary>
        void ReleaseArrivalSubscriptionLocked()
        {
            if (NeedsVolumeArrivalsLocked)
            {
                return;
            }

            _arrivalSubscription?.Dispose();
            _arrivalSubscription = null;
        }

        /// <summary>
        ///     Consumes the root's standing revival and, while its faulted instance and block are still
        ///     the root's, its watch is still requested and the index is not closing, queues a fresh
        ///     recovery ticket for it, which makes the root read <see cref="WatchState.Recovering" />.
        ///     The caller starts the returned ticket once it has released the state lock and raised
        ///     the state change; null when there was nothing to revive.
        /// </summary>
        RecoveryTicket? ReviveAbsentRecoveryLocked()
        {
            if (_absentRecovery is not { } absent)
            {
                return null;
            }

            _absentRecovery = null;
            if (IsClosing || !AnyRequestLocked || !ReferenceEquals(Current, absent.FailedInstance) ||
                absent.FailedInstance.State != WatchInstanceState.Faulted ||
                !ReferenceEquals(FindWatchableRootBlockLocked(), absent.FailedBlock))
            {
                ReleaseArrivalSubscriptionLocked();
                return null;
            }

            var revived = new RecoveryTicket(absent.FailedInstance, absent.FailedBlock, ClosingToken);
            Recovery = revived;
            _recoveryState = RecoveryState.Recovering;
            _recoveryCompletions.Add(revived.Completion);
            NoteWatchStateLocked();
            return revived;
        }

        /// <summary>
        ///     A volume arrived somewhere. An arrival names no root, so it wakes this root's recovery
        ///     pause, which probes the volume, or revives a recovery that gave up on an absent volume
        ///     while its faulted instance and block are still the root's and its watch is still
        ///     requested. The revived recovery is a fresh ticket on the usual path: the root reads
        ///     <see cref="WatchState.Recovering" /> and the same retry schedule applies. At most one
        ///     recovery runs per root, so repeated arrivals only wake its pauses.
        /// </summary>
        void OnVolumeArrival()
        {
            RecoveryTicket? revived;
            lock (StateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                if (Recovery is { } recovering)
                {
                    recovering.SignalArrival();
                    return;
                }

                revived = ReviveAbsentRecoveryLocked();
            }

            if (revived is null)
            {
                return;
            }

            RaiseWatchStateChanged();
            StartRecovery(revived);
        }
    }
}
