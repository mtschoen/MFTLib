namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The drive's <see cref="DriveWatchStatus.State" />, from its watch records:
    ///     <see cref="WatchState.Recovering" /> while a recovery of the drive is queued or
    ///     running or a scan of the watched drive retries after a lost catch-up, the current
    ///     instance's slot while it runs,
    ///     <see cref="WatchState.Faulted" /> for a faulted instance or a failed or refused
    ///     start, <see cref="WatchState.CatchingUp" /> while the watch is requested and its
    ///     replacement has not registered yet, and <see cref="WatchState.NotStarted" />
    ///     otherwise. The caller holds <see cref="_stateLock" />.
    /// </summary>
    WatchState GetWatchStateLocked(DriveKey driveLetter)
    {
        var runtime = GetDriveRuntime(driveLetter);
        if (runtime.RetryingLostCatchUp || runtime.RecoveryState == RecoveryState.Recovering)
        {
            return WatchState.Recovering;
        }

        if (runtime.Current is { } instance)
        {
            return instance.State == WatchInstanceState.Faulted
                ? WatchState.Faulted
                : instance.CatchUp.State;
        }

        if (runtime.RefusedStartFault is not null)
        {
            return WatchState.Faulted;
        }

        return runtime.WatchRequested ? WatchState.CatchingUp : WatchState.NotStarted;
    }

    /// <summary>
    ///     The task a wait on the drive follows: the fault of a lost catch-up being retried, the
    ///     current instance's catch-up, the fault of a failed or refused start, or, while the watch
    ///     is requested with no current instance, the drive's restart-pending waiter, which the
    ///     replacement instance adopts; null when the drive has no catch-up to wait for. The caller
    ///     holds <see cref="_stateLock" />.
    /// </summary>
    static Task? GetCatchUpWaitLocked(DriveRuntime runtime)
    {
        if (runtime.RetriedLostCatchUp is { } retried)
        {
            return Task.FromException(retried);
        }

        if (runtime.Current is { } instance)
        {
            return instance.CatchUp.Waiter.Task;
        }

        if (runtime.RefusedStartFault is { } refused)
        {
            return Task.FromException(refused);
        }

        return runtime.WatchRequested
            ? (runtime.RestartPendingWaiter ??=
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            : null;
    }

    /// <summary>
    ///     Hands the drive's restart-pending waiter, if any, to the instance being registered. The
    ///     caller holds <see cref="_stateLock" />.
    /// </summary>
    static TaskCompletionSource? TakeRestartPendingWaiterLocked(DriveRuntime runtime)
    {
        var waiter = runtime.RestartPendingWaiter;
        runtime.RestartPendingWaiter = null;
        return waiter;
    }

    /// <summary>
    ///     Faults the drive's restart-pending waiter, if any, with the reason no replacement watch
    ///     will adopt it. The caller holds <see cref="_stateLock" />.
    /// </summary>
    static void FaultRestartPendingWaiterLocked(DriveRuntime runtime, Exception exception)
    {
        if (TakeRestartPendingWaiterLocked(runtime) is { } waiter)
        {
            WatchCatchUpSlot.ReleaseFault(waiter, exception);
        }
    }

    /// <summary>
    ///     Cancels the drive's restart-pending waiter, if any, when a stop or disposal withdraws
    ///     the watch request. The caller holds <see cref="_stateLock" />.
    /// </summary>
    static void CancelRestartPendingWaiterLocked(DriveRuntime runtime)
    {
        TakeRestartPendingWaiterLocked(runtime)?.TrySetCanceled();
    }

}
