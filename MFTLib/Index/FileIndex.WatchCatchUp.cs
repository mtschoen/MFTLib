namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The drive's <see cref="DriveStatus.WatchCatchUp" />, from its watch records: the current
    ///     instance's slot while it runs, <see cref="WatchCatchUpState.Faulted" /> for a faulted
    ///     instance or a refused start, and <see cref="WatchCatchUpState.NotStarted" /> otherwise.
    ///     The caller holds <see cref="_stateLock" />.
    /// </summary>
    WatchCatchUpState GetWatchCatchUpStateLocked(char driveLetter)
    {
        if (!_driveRuntimes.TryGetValue(char.ToUpperInvariant(driveLetter), out var runtime))
        {
            return WatchCatchUpState.NotStarted;
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
    ///     Waits for one drive's initial journal catch-up: the returned task completes once the
    ///     backlog present when the drive's current watch started has been applied and the drive
    ///     is on live entries. It completes immediately when the drive is already caught up,
    ///     faults with the drive's exception when its watch fails (before or after this call) or
    ///     its last start failed, and is cancelled when that watch is stopped, retired by a
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
            if (runtime.Current is { } instance)
            {
                wait = instance.CatchUp.Waiter.Task;
            }
            else if (runtime.RefusedStartFault is { } refused)
            {
                wait = Task.FromException(refused);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Drive {driveLetter} is not being watched, so there is no catch-up to wait for.");
            }
        }

        return AwaitQueuedAsync(wait, cancellationToken, DisposalToken);
    }
}
