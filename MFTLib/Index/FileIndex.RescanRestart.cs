namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The body of <see cref="RescanAsync(char, CancellationToken)" /> once it holds the drive's lifecycle gate, and of
    ///     a recovery (<paramref name="recovery" />): run the scan operation, retiring the watch
    ///     at publication, then restart it when it is still requested. A manual rescan first
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

        ScanAttempt outcome;
        try
        {
            outcome = await RunScanOperationAsync(runtime, drive, recovery, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not JournalCatchUpLostException)
        {
            _ = RefuseWatchOverUnresumableBlock(runtime);
            throw;
        }

        if (outcome.Published)
        {
            ClearRefusedStartAfterReplacement(runtime);
            await RestartRequestedWatchAsync(runtime, recovery).ConfigureAwait(false);
            return;
        }

        _ = RefuseWatchOverUnresumableBlock(runtime);
        throw new InvalidOperationException(
            $"Drive {driveLetter} was not rescanned: {outcome.ProducerFailureMessage}", outcome.ProducerFailure);
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
    ///     Captures the old pump and any predecessor's teardown while retiring a healthy current
    ///     watch in the publication's state-lock section. A faulted current watch stays until
    ///     restart registration supersedes it. A retired watch's subscriber fault stays on the
    ///     runtime for stop to take even after the pump drains. The returned drains never fault.
    /// </summary>
    static WatchHandoff RetireWatchAtCommitLocked(DriveRuntime runtime)
    {
        var predecessorDrain = runtime.Retiring?.Drained ?? Task.CompletedTask;
        var currentDrain = runtime.Current?.Drained ?? Task.CompletedTask;
        var retired = runtime.Current is { State: WatchInstanceState.Starting or WatchInstanceState.Running }
            ? RetireCurrentLocked(runtime)
            : null;
        if (retired is not null)
        {
            runtime.RescanHandoffFault ??= retired.OutstandingFault;
            retired.OutstandingFault = null;
        }

        return new WatchHandoff(retired, Task.WhenAll(predecessorDrain, currentDrain));
    }

    readonly record struct WatchHandoff(WatchInstance? Retired, Task Drained)
    {
        /// <summary>Called after publication, with only the drive's lifecycle gate held.</summary>
        public Task DrainAsync()
        {
            Retired?.RequestStop();
            return Drained;
        }
    }

    /// <summary>
    ///     A replacement block has a fresh cursor, so a start refused or failed over the old block
    ///     describes the replaced block. A faulted current watch is left for the restart that
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
    ///     restarted watch as awaiting its first catch-up. Disposal cancels the operation at
    ///     this checkpoint even when the watch is not requested; a recovery drops it quietly.
    /// </summary>
    Task RestartRequestedWatchAsync(DriveRuntime runtime, RecoveryTicket? recovery)
    {
        BeforeRestartDecisionForTest?.Invoke(runtime.DriveLetter);
        bool requested;
        lock (_stateLock)
        {
            ThrowIfCancelledByDisposal(CancellationToken.None);

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

    /// <summary>
    ///     Retains a failed rescan restart as a faulted instance, so stop takes its outstanding
    ///     fault once. A stop or disposal that already retired it wins and records no new fault.
    ///     The scan has committed; this failure belongs only to the watch.
    /// </summary>
    bool RecordRescanRestartFailure(DriveRuntime runtime, WatchInstance instance, Exception startFailure)
    {
        var failure = new InvalidOperationException(
            $"A rescan replaced drive {runtime.DriveLetter}'s block and the watch could not be started on it.",
            startFailure);
        lock (_stateLock)
        {
            if (_disposed || !ReferenceEquals(runtime.Current, instance))
            {
                return false;
            }

            instance.State = WatchInstanceState.Faulted;
            runtime.RescanHandoffFault = null;
            instance.OutstandingFault = failure;
            instance.CatchUp.Fault(failure);
            _watchFailureMessagesByOrdinal[instance.ArmedBlock.DriveOrdinal] = failure.Message;
        }

        CompleteInstanceDrain(runtime, instance);
        RaiseWatchFaulted(new WatchFault(WatchFaultKind.RescanRestart, runtime.DriveLetter, failure));
        return true;
    }
}
