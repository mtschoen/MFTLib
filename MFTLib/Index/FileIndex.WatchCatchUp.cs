namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The drive's <see cref="DriveStatus.WatchCatchUp" />, from its watch records:
    ///     <see cref="WatchCatchUpState.Recovering" /> while a recovery of the drive is queued or
    ///     running or a scan of the watched drive retries after a lost catch-up, the current
    ///     instance's slot while it runs,
    ///     <see cref="WatchCatchUpState.Faulted" /> for a faulted instance or a refused start, and
    ///     <see cref="WatchCatchUpState.NotStarted" /> otherwise. The caller holds
    ///     <see cref="_stateLock" />.
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

        return runtime.RefusedStartFault is null ? WatchCatchUpState.NotStarted : WatchCatchUpState.Faulted;
    }

    /// <summary>
    ///     The task a wait on the drive follows: the fault of a lost catch-up being retried, the
    ///     current instance's catch-up, or the fault of a refused start; null when the drive has no
    ///     catch-up to wait for. The caller holds <see cref="_stateLock" />.
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

        return runtime.RefusedStartFault is { } refused ? Task.FromException(refused) : null;
    }

    /// <summary>
    ///     Waits for one drive's initial journal catch-up: the returned task completes once the
    ///     backlog present when the drive's current watch started has been applied and the drive
    ///     is on live entries. It completes immediately when the drive is already caught up,
    ///     faults with the drive's exception when its watch fails (before or after this call) or
    ///     its last start failed, faults at once while the drive reads
    ///     <see cref="WatchCatchUpState.Recovering" /> (with the fault that ended its watch, or
    ///     the lost catch-up a scan is retrying; a consumer that wants the recovered watch waits
    ///     again once the drive reads <see cref="WatchCatchUpState.CatchingUp" />), and is
    ///     cancelled when that watch is stopped, retired by a
    ///     rescan, or the index is disposed. <paramref name="cancellationToken" /> and the index's
    ///     disposal cancel the wait itself without touching the drive's watch. The wait's
    ///     continuation never runs inline on the thread that settles it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="driveLetter" /> is not part of this index.</exception>
    /// <exception cref="InvalidOperationException">The drive has no current watch.</exception>
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
