using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Stops one drive's watch and waits for its teardown, then rethrows, once, the fault that
    ///     ended the watch or the first subscriber fault it announced, if either is outstanding.
    ///     Takes no lifecycle gate, so it never waits for a rescan: a stop during a rescan of the
    ///     same drive completes while the scan runs, and the rescan then leaves the watch stopped.
    ///     The same holds for an automatic recovery, and a queued one ends without scanning.
    ///     A manual rescan retains the retired watch's fault through its drain and replacement
    ///     start, so a stop before the replacement handle is published still takes that fault.
    ///     The drive's <see cref="DriveWatchStatus.CatchUpState" /> reads
    ///     <see cref="WatchCatchUpState.NotStarted" /> afterwards and any pending catch-up wait is
    ///     cancelled. A drive counts as watching while its watch is requested or it has a watch
    ///     instance, current or still retiring; a start that failed at its source or was refused
    ///     over an unresumable block leaves the watch requested, so stopping that drive clears the
    ///     request and its faulted state.
    ///     <paramref name="cancellationToken" /> bounds only the wait for the teardown: cancelling
    ///     it throws while the teardown continues, and a later start waits for that teardown.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="driveLetter" /> is not part of this index.</exception>
    /// <exception cref="InvalidOperationException">The drive is not watching.</exception>
    internal async Task StopWatchingAsync(char driveLetter, CancellationToken cancellationToken)
    {
        if (RejectInsideHandler(nameof(StopWatchingAsync)) is { } rejection)
        {
            throw rejection;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = GetDriveRuntime(driveLetter);
        WatchInstance? retired;
        Task? previousDrain;
        Exception? outstandingFault;
        lock (_stateLock)
        {
            if (!IsWatchingLocked(runtime))
            {
                throw new InvalidOperationException(
                    $"Drive {runtime.DriveLetter} is not watching, so there is no watch to stop.");
            }

            runtime.WatchRequested = false;
            runtime.RefusedStartFault = null;
            CancelRestartPendingWaiterLocked(runtime);
            ClearRecoveryLocked(runtime);
            previousDrain = runtime.Retiring?.Drained;
            retired = RetireCurrentLocked(runtime);
            outstandingFault = TakeOutstandingFaultLocked(runtime, retired);
            NoteWatchStateLocked(runtime);
        }

        RaiseWatchStateChanged(runtime);
        retired?.RequestStop();
        var drain = retired?.Drained is { } retiredDrain && previousDrain is not null
            ? Task.WhenAll(retiredDrain, previousDrain)
            : retired?.Drained ?? previousDrain;
        if (drain is not null)
        {
            await AwaitQueuedAsync(drain, cancellationToken, CancellationToken.None).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            outstandingFault ??= TakeOutstandingFaultLocked(runtime, retired);
        }

        if (outstandingFault is not null)
        {
            ExceptionDispatchInfo.Capture(outstandingFault).Throw();
        }
    }

    /// <summary>
    ///     Consumes any pending subscriber or handoff fault atomically across the instance being
    ///     stopped, any concurrent retiring predecessor, and the runtime handoff slot.
    /// </summary>
    static Exception? TakeOutstandingFaultLocked(DriveRuntime runtime, WatchInstance? retired)
    {
        var fault = retired?.OutstandingFault ?? runtime.Retiring?.OutstandingFault ?? runtime.RescanHandoffFault;
        if (fault is null)
        {
            return null;
        }

        if (retired is not null)
        {
            retired.OutstandingFault = null;
        }

        if (runtime.Retiring is not null)
        {
            runtime.Retiring.OutstandingFault = null;
        }

        runtime.RescanHandoffFault = null;
        return fault;
    }
}
