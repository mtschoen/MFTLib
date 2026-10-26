using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>Withdraws this drive's request and drains its watch without taking the lifecycle gate.</summary>
        public async Task StopDriveWatchingAsync(CancellationToken cancellationToken)
        {
            if (Index.RejectInsideHandler(nameof(StopWatchingAsync)) is { } rejection)
            {
                throw rejection;
            }

            ObjectDisposedException.ThrowIf(IsDisposed, Index);
            WatchInstance? retired;
            Task? previousDrain;
            Exception? outstandingFault;
            lock (StateLock)
            {
                if (!IsWatchingLocked())
                {
                    throw new InvalidOperationException(
                        $"Drive {Drive} is not watching, so there is no watch to stop.");
                }

                _watchRequested = false;
                _refusedStartFault = null;
                CancelRestartPendingWaiterLocked();
                ClearRecoveryLocked();
                previousDrain = _retiring?.Drained;
                retired = RetireCurrentLocked();
                outstandingFault = TakeOutstandingFaultLocked(retired);
                NoteWatchStateLocked();
            }

            RaiseWatchStateChanged();
            retired?.RequestStop();
            var drain = retired?.Drained is { } retiredDrain && previousDrain is not null
                ? Task.WhenAll(retiredDrain, previousDrain)
                : retired?.Drained ?? previousDrain;
            if (drain is not null)
            {
                await AwaitQueuedAsync(drain, cancellationToken, CancellationToken.None).ConfigureAwait(false);
            }

            lock (StateLock)
            {
                outstandingFault ??= TakeOutstandingFaultLocked(retired);
            }

            if (outstandingFault is not null)
            {
                ExceptionDispatchInfo.Capture(outstandingFault).Throw();
            }
        }

        Exception? TakeOutstandingFaultLocked(WatchInstance? retired)
        {
            var fault = retired?.OutstandingFault ?? _retiring?.OutstandingFault ?? _rescanHandoffFault;
            if (fault is null)
            {
                return null;
            }

            retired?.OutstandingFault = null;
            _retiring?.OutstandingFault = null;

            _rescanHandoffFault = null;
            return fault;
        }

        /// <summary>
        ///     Moves the drive's current instance to retiring, cancels its catch-up slot, and returns
        ///     it, or returns null when the drive has none. The caller holds <see cref="StateLock" />
        ///     and calls <see cref="WatchInstance.RequestStop" /> on the result after releasing it.
        ///     An instance whose teardown already finished (a faulted one whose pump has returned) goes
        ///     straight out, since there is nothing left for a successor to wait for.
        /// </summary>
        WatchInstance? RetireCurrentLocked()
        {
            if (Current is not { } instance)
            {
                return null;
            }

            Current = null;
            instance.CatchUp.Cancel();
            if (instance.Drained.IsCompleted)
            {
                return instance;
            }

            instance.State = WatchInstanceState.Retiring;
            _retiring = instance;
            return instance;
        }

        /// <summary>
        ///     Finishes an instance's teardown. An instance that is still current keeps its state (a
        ///     faulted instance stays faulted and current until a stop, a start, or a rescan replaces
        ///     it). The retiring instance is detached when its teardown completes.
        /// </summary>
        void CompleteInstanceDrain(WatchInstance instance)
        {
            lock (StateLock)
            {
                if (ReferenceEquals(_retiring, instance))
                {
                    _retiring = null;
                    NoteWatchStateLocked();
                }
            }

            instance.CompleteDrain();
            RaiseWatchStateChanged();
        }
    }
}
