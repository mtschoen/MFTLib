namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The body of <see cref="RescanAsync(char, CancellationToken)" /> once it holds the drive's lifecycle gate, and of
    ///     a recovery (<paramref name="recovery" />): retire the drive's watch, run the scan
    ///     operation, then restart the watch when it is still requested. A manual rescan first
    ///     clears the drive's recovery ticket, which it supersedes.
    /// </summary>
    async Task RescanWithGateHeldAsync(DriveRuntime runtime, IndexedDrive drive, RecoveryTicket? recovery,
        CancellationToken cancellationToken)
    {
        var driveLetter = runtime.DriveLetter;
        if (recovery is null)
        {
            lock (_stateLock)
            {
                ClearRecoveryLocked(runtime);
            }
        }

        var requiresReplacement = RequiresReplacementForWatchRecovery(driveLetter);
        await RetireWatchForRescanAsync(runtime).ConfigureAwait(false);

        ScanAttempt outcome;
        try
        {
            outcome = await RunScanOperationAsync(runtime, drive, recovery, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception scanFailure) when (scanFailure is not JournalCatchUpLostException)
        {
            await ResumeAfterFailedScanAsync(runtime, requiresReplacement, scanFailure).ConfigureAwait(false);
            throw;
        }

        if (outcome.Published)
        {
            ClearRefusedStartAfterReplacement(runtime);
            await RestartRequestedWatchAsync(runtime, recovery, scanPublished: true).ConfigureAwait(false);
            return;
        }

        var noBlock = new InvalidOperationException(
            $"Drive {driveLetter} was not rescanned: {outcome.ProducerFailureMessage}", outcome.ProducerFailure);
        await ResumeAfterFailedScanAsync(runtime, requiresReplacement, noBlock).ConfigureAwait(false);
        throw noBlock;
    }

    /// <summary>
    ///     Restarts a drive whose watch was healthy before a rescan whose last attempt replaced
    ///     nothing, from the cursor of the block now published, when the watch is still requested.
    ///     Judged after the attempts, not before them: when an earlier attempt of the same operation
    ///     published a block whose catch-up was lost, the old cursor is gone and the block in place
    ///     cannot be resumed, so the watch is refused the way a start would refuse it and nothing is
    ///     restarted. A drive that already required a replacement stays as it was. A restart that
    ///     fails is reported together with the scan's failure.
    /// </summary>
    async Task ResumeAfterFailedScanAsync(DriveRuntime runtime, bool requiredReplacement, Exception scanFailure)
    {
        if (requiredReplacement || RefuseWatchOverUnresumableBlock(runtime))
        {
            return;
        }

        try
        {
            await RestartRequestedWatchAsync(runtime, recovery: null, scanPublished: false).ConfigureAwait(false);
        }
        catch (Exception restartFailure)
        {
            throw new AggregateException(
                $"Drive {runtime.DriveLetter} could not restart its watch after its rescan failed, so its watch is stopped.",
                scanFailure, restartFailure);
        }
    }

    /// <summary>
    ///     True when the drive's published block cannot be resumed; a watch that is still requested
    ///     then records the refusal <see cref="StartWatchingAsync(char, CancellationToken)" /> would record, without a start.
    /// </summary>
    bool RefuseWatchOverUnresumableBlock(DriveRuntime runtime)
    {
        lock (_stateLock)
        {
            if (FindWatchableDriveBlockLocked(runtime.DriveLetter) is not { } driveBlock ||
                !_unresumableCheckpointsByOrdinal.TryGetValue(driveBlock.DriveOrdinal, out var reason))
            {
                return false;
            }

            if (runtime.WatchRequested)
            {
                _ = RecordUnresumableCheckpointWatchFailureLocked(runtime, driveBlock, reason);
            }

            return true;
        }
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
    ///     still requested and the index is not being disposed. Not bounded by the rescan's token;
    ///     see <see cref="RescanAsync(char, CancellationToken)" />. A recovery's restart names its ticket, which marks the
    ///     restarted watch as awaiting its first catch-up. After a scan that published its block
    ///     (<paramref name="scanPublished" />), a disposal that has begun cancels the operation, as
    ///     it does at every earlier checkpoint, whether or not the watch is still requested; a
    ///     recovery drops that cancellation quietly. After a failed scan the operation already ends
    ///     with the scan's failure, so a disposal only skips the restart.
    /// </summary>
    Task RestartRequestedWatchAsync(DriveRuntime runtime, RecoveryTicket? recovery, bool scanPublished)
    {
        BeforeRestartDecisionForTest?.Invoke(runtime.DriveLetter);
        bool requested;
        lock (_stateLock)
        {
            if (scanPublished)
            {
                ThrowIfCancelledByDisposal(CancellationToken.None);
            }

            requested = runtime.WatchRequested && !_disposed;
        }

        if (!requested)
        {
            return Task.CompletedTask;
        }

        RestartRequestedForTest?.Invoke(runtime.DriveLetter);
        return StartWatchingCoreAsync(runtime, gateHeld: true, recovery, CancellationToken.None);
    }

    /// <summary>
    ///     A test seam: invoked with the drive letter once a rescan's or a recovery's restart has
    ///     read that the watch is still requested, before the start re-reads it.
    /// </summary>
    internal Action<char>? RestartRequestedForTest { get; set; }

    /// <summary>
    ///     A test seam: invoked with the drive letter once a rescan or a recovery has finished its
    ///     scans, before its restart reads whether the index is being disposed and whether the
    ///     watch is still requested.
    /// </summary>
    internal Action<char>? BeforeRestartDecisionForTest { get; set; }
}
