namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The drive's <see cref="DriveStatus.WatchCatchUpState" />, from its watch records:
    ///     <see cref="WatchCatchUpState.Recovering" /> while a recovery of the drive is queued or
    ///     running or a scan of the watched drive retries after a lost catch-up, the current
    ///     instance's slot while it runs,
    ///     <see cref="WatchCatchUpState.Faulted" /> for a faulted instance or a failed or refused
    ///     start, <see cref="WatchCatchUpState.CatchingUp" /> while the watch is requested and its
    ///     replacement has not registered yet, and <see cref="WatchCatchUpState.NotStarted" />
    ///     otherwise. The caller holds <see cref="_stateLock" />.
    /// </summary>
    WatchCatchUpState GetWatchCatchUpStateLocked(char driveLetter)
    {
        var runtime = GetDriveRuntime(driveLetter);
        if (runtime.RetryingLostCatchUp || runtime.RecoveryState == RecoveryState.Recovering)
        {
            return WatchCatchUpState.Recovering;
        }

        if (runtime.Current is { } instance)
        {
            return instance.State == WatchInstanceState.Faulted
                ? WatchCatchUpState.Faulted
                : instance.CatchUp.State;
        }

        if (runtime.RefusedStartFault is not null)
        {
            return WatchCatchUpState.Faulted;
        }

        return runtime.WatchRequested ? WatchCatchUpState.CatchingUp : WatchCatchUpState.NotStarted;
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

    /// <summary>
    ///     Waits for one drive's initial journal catch-up: the returned task completes once the
    ///     backlog present when the drive's current watch started has been applied and the drive
    ///     is on live entries. It completes immediately when the drive is already caught up,
    ///     faults with the drive's exception when its watch fails (before or after this call) or
    ///     its last start failed or was refused, faults at once while the drive reads
    ///     <see cref="WatchCatchUpState.Recovering" /> (with the fault that ended its watch, or
    ///     the lost catch-up a scan is retrying; a consumer that wants the recovered watch waits
    ///     again once the drive reads <see cref="WatchCatchUpState.CatchingUp" />, which
    ///     <see cref="WatchStateChanged" /> reports), and is
    ///     cancelled when that watch is stopped, retired by a
    ///     rescan, or the index is disposed. A wait issued after a rescan retired the drive's
    ///     requested watch and before its replacement registered follows the replacement: it
    ///     completes with the replacement's catch-up and faults when the replacement cannot start.
    ///     When the wait faults because the watch failed, <see cref="Drives" /> already reflects
    ///     that fault and any checkpoint loss it found, and, for a fault that queues an automatic
    ///     recovery, the drive reads <see cref="WatchCatchUpState.Recovering" />; a fault that
    ///     recovers no further leaves it <see cref="WatchCatchUpState.Faulted" />, and a stop that
    ///     overtook it leaves it <see cref="WatchCatchUpState.NotStarted" />. <paramref name="cancellationToken" /> and the index's
    ///     disposal cancel the wait itself without touching the drive's watch. The wait's
    ///     continuation never runs inline on the thread that settles it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="driveLetter" /> is not part of this index.</exception>
    /// <exception cref="InvalidOperationException">The drive's watch is not requested and it has no current watch.</exception>
    public Task WaitForCatchUpAsync(char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = GetDriveRuntime(driveLetter);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Task wait;
        lock (_stateLock)
        {
            wait = GetCatchUpWaitLocked(runtime) ?? throw new InvalidOperationException(
                $"Drive {driveLetter} is not being watched, so there is no catch-up to wait for.");
        }

        if (!wait.IsCompleted && RejectedTask(nameof(WaitForCatchUpAsync)) is { } rejected)
        {
            return rejected;
        }

        return AwaitQueuedAsync(wait, cancellationToken, DisposalToken);
    }
}
