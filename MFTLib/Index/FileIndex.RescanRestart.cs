namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The body of <see cref="RescanAsync" /> once it holds the drive's lifecycle gate: retire
    ///     the drive's watch, produce and commit the new block, then restart the watch when it is
    ///     still requested.
    /// </summary>
    async Task RescanWithGatesHeldAsync(DriveRuntime runtime, IndexedDrive drive, CancellationToken cancellationToken)
    {
        var driveLetter = runtime.DriveLetter;
        var requiresReplacement = RequiresReplacementForWatchRecovery(driveLetter);
        await RetireWatchForRescanAsync(runtime).ConfigureAwait(false);

        BlockReplacementOutcome replacement;
        try
        {
            replacement = await SwapDriveBlockAsync(drive, driveLetter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception swapFailure) when (!requiresReplacement)
        {
            try
            {
                await RestartRequestedWatchAsync(runtime).ConfigureAwait(false);
            }
            catch (Exception restartFailure)
            {
                throw new AggregateException(
                    $"Drive {driveLetter} could not restart its watch after its rescan failed, so its watch is stopped.",
                    swapFailure, restartFailure);
            }

            throw;
        }

        if (replacement == BlockReplacementOutcome.Replaced)
        {
            ClearRefusedStartAfterReplacement(runtime);
        }
        else if (requiresReplacement)
        {
            return;
        }

        await RestartRequestedWatchAsync(runtime).ConfigureAwait(false);
    }

    /// <summary>
    ///     Retires the drive's starting or running watch and waits for its teardown, and for any
    ///     earlier instance still retiring, before the scan. A faulted watch stays current, since
    ///     its pump has already ended: a rescan that produces no replacement leaves the drive
    ///     faulted exactly as it was, and a restart after a replacement supersedes it.
    /// </summary>
    async Task RetireWatchForRescanAsync(DriveRuntime runtime)
    {
        WatchInstance? retired = null;
        var drains = new List<Task>(2);
        lock (_stateLock)
        {
            if (runtime.Retiring is { } alreadyRetiring)
            {
                drains.Add(alreadyRetiring.Drained);
            }

            if (runtime.Current is { State: WatchInstanceState.Starting or WatchInstanceState.Running })
            {
                retired = RetireCurrentLocked(runtime);
            }

            if ((retired ?? runtime.Current) is { } instance)
            {
                drains.Add(instance.Drained);
            }
        }

        retired?.RequestStop();

        // Not bounded by the rescan's token: the stop request already ends the pump's read, so the
        // drain is prompt, and a rescan that gave up here would leave a healthy watch stopped with
        // nothing to restart it. A cancellation is observed by the scan instead, whose failure
        // restarts a healthy watch from its old cursor.
        foreach (var drain in drains)
        {
            await drain.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     A replacement block has a fresh cursor, so a start refused or failed over the old block
    ///     no longer describes the drive. A faulted current watch is left for the restart that
    ///     follows, which supersedes it.
    /// </summary>
    void ClearRefusedStartAfterReplacement(DriveRuntime runtime)
    {
        lock (_stateLock)
        {
            if (runtime.Current is not null)
            {
                return;
            }

            runtime.RefusedStartFault = null;
            if (TryGetDriveOrdinalLocked(runtime.DriveLetter, out var driveOrdinal))
            {
                _watchFailureMessagesByOrdinal.Remove(driveOrdinal);
            }
        }
    }

    /// <summary>
    ///     Starts the drive's watch again, with the lifecycle gate the rescan holds, when it is
    ///     still requested. Not bounded by the rescan's token; see <see cref="RescanAsync" />.
    /// </summary>
    Task RestartRequestedWatchAsync(DriveRuntime runtime)
    {
        bool requested;
        lock (_stateLock)
        {
            requested = runtime.WatchRequested;
        }

        return requested
            ? StartWatchingCoreAsync(runtime, gateHeld: true, CancellationToken.None)
            : Task.CompletedTask;
    }
}
