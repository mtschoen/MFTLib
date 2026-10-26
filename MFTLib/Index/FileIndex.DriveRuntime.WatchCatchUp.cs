namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     The task a wait on the drive follows: the fault of a lost catch-up being retried, the
        ///     current instance's catch-up, the fault of a failed or refused start, or, while the watch
        ///     is requested with no current instance, the drive's restart-pending waiter, which the
        ///     replacement instance adopts; null when the drive has no catch-up to wait for. The caller
        ///     holds <see cref="StateLock" />.
        /// </summary>
        public Task? GetCatchUpWaitLocked()
        {
            if (_retriedLostCatchUp is { } retried)
            {
                return Task.FromException(retried);
            }

            if (Current is { } instance)
            {
                return instance.CatchUp.Waiter.Task;
            }

            if (_refusedStartFault is { } refused)
            {
                return Task.FromException(refused);
            }

            return _watchRequested
                ? (_restartPendingWaiter ??=
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : null;
        }

        /// <summary>
        ///     Hands the drive's restart-pending waiter, if any, to the instance being registered. The
        ///     caller holds <see cref="StateLock" />.
        /// </summary>
        TaskCompletionSource? TakeRestartPendingWaiterLocked()
        {
            var waiter = _restartPendingWaiter;
            _restartPendingWaiter = null;
            return waiter;
        }

        /// <summary>
        ///     Faults the drive's restart-pending waiter, if any, with the reason no replacement watch
        ///     will adopt it. The caller holds <see cref="StateLock" />.
        /// </summary>
        void FaultRestartPendingWaiterLocked(Exception exception)
        {
            if (TakeRestartPendingWaiterLocked() is { } waiter)
            {
                WatchCatchUpSlot.ReleaseFault(waiter, exception);
            }
        }

        /// <summary>
        ///     Cancels the drive's restart-pending waiter, if any, when a stop or disposal withdraws
        ///     the watch request. The caller holds <see cref="StateLock" />.
        /// </summary>
        void CancelRestartPendingWaiterLocked()
        {
            TakeRestartPendingWaiterLocked()?.TrySetCanceled();
        }
    }
}
