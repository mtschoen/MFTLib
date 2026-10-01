namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     A test seam: when set, the pump calls it as <c>wrapper(settleFault)</c> around exactly
    ///     the step that records a drive's fault and marks that drive's catch-up slot faulted,
    ///     before its catch-up waiter is faulted and <see cref="WatchFaulted" /> is raised, so a
    ///     test can observe what runs on the settling stack.
    /// </summary>
    internal Action<Action>? PumpFaultSettlementWrapperForTest { get; set; }

    /// <summary>A test seam: invoked after apply releases the write gate, before Changed is raised.</summary>
    internal Action<char>? BeforeWatchChangedForTest { get; set; }

    /// <summary>
    ///     One drive's pump: reads the drive's handle until it is stopped or faults, applies each
    ///     batch, and records a fault only while its instance is still the drive's current one. On
    ///     every exit it is the handle's only disposer, and it completes the instance's
    ///     <see cref="WatchInstance.Drained" /> last.
    /// </summary>
    async Task PumpAsync(DriveRuntime runtime, WatchInstance instance, IIndexDriveWatch handle)
    {
        try
        {
            if (await ReadWatchAsync(runtime, instance, handle).ConfigureAwait(false) is { } fault)
            {
                RecordPumpFault(runtime, instance, fault);
            }
        }
        finally
        {
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The drive's watch is over either way, and a stop or disposal waiting on this
                // teardown has no one to hand the failure to. Discarded through the variable
                // rather than an empty body, which is this repository's idiom for a deliberate
                // swallow and what keeps RCS1075 honest here.
                _ = exception;
            }
            finally
            {
                CompleteInstanceDrain(runtime, instance);
            }
        }
    }

    /// <summary>
    ///     Reads the handle to its end and classifies that end: null for a stop, otherwise the
    ///     fault that ended the watch.
    /// </summary>
    async Task<WatchFault?> ReadWatchAsync(DriveRuntime runtime, WatchInstance instance, IIndexDriveWatch handle)
    {
        var driveLetter = runtime.DriveLetter;
        var stopToken = instance.PumpStop.Token;
        try
        {
            await foreach (var item in handle.ReadAsync(stopToken).ConfigureAwait(false))
            {
                if (item is JournalBatch batch)
                {
                    if (ApplyWatchBatch(runtime, instance, batch) is { } applyFailure)
                    {
                        return new WatchFault(WatchFaultKind.Apply, driveLetter, applyFailure);
                    }
                }
                else if (item is DriveCaughtUp)
                {
                    CompleteWatchCatchUp(runtime, instance);
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return null;
        }
        catch (DriveWatchFaultException exception)
        {
            return new WatchFault(WatchFaultKind.Drive, driveLetter, exception);
        }
        catch (Exception exception)
        {
            return new WatchFault(WatchFaultKind.Channel, driveLetter, exception);
        }

        return stopToken.IsCancellationRequested
            ? null
            : new WatchFault(WatchFaultKind.Channel, driveLetter,
                new InvalidOperationException($"The watch for drive {driveLetter} ended without being stopped."));
    }

    /// <summary>
    ///     Applies one batch, then raises <see cref="Changed" /> with no gate or lock held. Returns
    ///     the apply failure, which ends the drive's watch; a subscriber failure does not.
    /// </summary>
    Exception? ApplyWatchBatch(DriveRuntime runtime, WatchInstance instance, JournalBatch batch)
    {
        IReadOnlyList<FileChange> changes;
        try
        {
            changes = ApplyJournalEntriesCore(runtime.DriveLetter, instance, batch.Entries, batch.JournalId,
                batch.NextUsn);
        }
        catch (Exception exception)
        {
            return exception;
        }

        try
        {
            BeforeWatchChangedForTest?.Invoke(runtime.DriveLetter);
            RaiseChanged(changes);
        }
        catch (Exception exception)
        {
            AnnounceSubscriberFault(runtime, instance, exception);
        }

        return null;
    }

    /// <summary>
    ///     Announces a subscriber fault once per instance and keeps the first as the instance's
    ///     outstanding fault, unless one is already held. The drive keeps watching. A delayed fault
    ///     from a watch retired during rescan publication is also retained for stop handoff.
    /// </summary>
    void AnnounceSubscriberFault(DriveRuntime runtime, WatchInstance instance, Exception exception)
    {
        bool first;
        lock (_stateLock)
        {
            first = !instance.SubscriberFaultAnnounced &&
                    (ReferenceEquals(runtime.Current, instance) || ReferenceEquals(runtime.Retiring, instance) ||
                     instance.State == WatchInstanceState.Retiring);
            if (first)
            {
                instance.SubscriberFaultAnnounced = true;
                instance.OutstandingFault ??= exception;
                if (!ReferenceEquals(runtime.Current, instance))
                {
                    runtime.RescanHandoffFault ??= exception;
                }
            }
        }

        if (first)
        {
            RaiseWatchFaulted(new WatchFault(WatchFaultKind.Subscriber, runtime.DriveLetter, exception));
        }
    }

    void CompleteWatchCatchUp(DriveRuntime runtime, WatchInstance instance)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(runtime.Current, instance) && instance.State == WatchInstanceState.Running)
            {
                instance.CatchUp.Complete();
                if (runtime.RecoveryState == RecoveryState.RecoveredAwaitingCatchUp)
                {
                    runtime.RecoveryState = RecoveryState.None;
                }

                NoteWatchStateLocked(runtime);
            }
        }

        RaiseWatchStateChanged(runtime);
    }

    /// <summary>
    ///     Records the fault that ended a watch, only while its instance is still the drive's
    ///     current one and the index is not being disposed: the failure message, the outstanding
    ///     fault, the faulted slot, and the faulted state. Then the checkpoint-loss check, then,
    ///     for a <see cref="WatchFaultKind.Drive" /> or <see cref="WatchFaultKind.Apply" /> fault,
    ///     the recovery ticket that makes the drive read <see cref="WatchCatchUpState.Recovering" />,
    ///     then the catch-up waiter's fault, then <see cref="WatchFaulted" />, so a waiter or a
    ///     handler that reads <see cref="Drives" /> already sees why the drive stopped and that it
    ///     is recovering, and only then the recovery itself.
    ///     A <see cref="WatchFaultKind.Channel" /> fault never recovers. A watch a recovery
    ///     restarted that faults before it first catches up queues no further recovery: its
    ///     <see cref="WatchFaultKind.Drive" /> or <see cref="WatchFaultKind.Apply" /> fault is
    ///     raised as <see cref="WatchFaultKind.Recovery" />.
    /// </summary>
    void RecordPumpFault(DriveRuntime runtime, WatchInstance instance, WatchFault fault)
    {
        var recovers = fault.Kind is WatchFaultKind.Drive or WatchFaultKind.Apply;
        var recoveryFailed = false;
        var reported = fault;
        TaskCompletionSource? faultedWaiter = null;

        void Settle()
        {
            lock (_stateLock)
            {
                if (_disposed || !ReferenceEquals(runtime.Current, instance) ||
                    instance.State != WatchInstanceState.Running)
                {
                    return;
                }

                instance.State = WatchInstanceState.Faulted;
                instance.OutstandingFault ??= fault.Exception;
                _watchFailureMessagesByOrdinal[instance.ArmedBlock.DriveOrdinal] = fault.Exception.Message;
                faultedWaiter = instance.CatchUp.MarkFaulted(fault.Exception);
                recoveryFailed = runtime.RecoveryState == RecoveryState.RecoveredAwaitingCatchUp;
                if (recoveryFailed)
                {
                    runtime.RecoveryState = RecoveryState.None;
                    if (recovers)
                    {
                        reported = fault with { Kind = WatchFaultKind.Recovery };
                    }
                }

                NoteWatchStateLocked(runtime, reported);
            }
        }

        if (PumpFaultSettlementWrapperForTest is { } wrapper)
        {
            wrapper(Settle);
        }
        else
        {
            Settle();
        }

        if (faultedWaiter is null)
        {
            return;
        }

        RecoveryTicket? recovery;
        try
        {
            RecordCheckpointLossForFaultedDrive(runtime, instance);
            recovery = recovers && !recoveryFailed ? QueueRecovery(runtime, instance, fault) : null;
        }
        finally
        {
            WatchCatchUpSlot.ReleaseFault(faultedWaiter, fault.Exception);

            // Delivers what the settlement noted even when the checkpoint check threw.
            RaiseWatchStateChanged(runtime);
        }

        RaiseWatchFaulted(reported);
        if (recovery is not null)
        {
            StartRecovery(runtime, recovery);
        }
    }

    void RaiseWatchFaulted(WatchFault fault)
    {
        RaiseWatchStateChanged(GetDriveRuntime(fault.DriveLetter));
        try
        {
            if (WatchFaulted is { } subscribers)
            {
                DeliverToEach(subscribers, fault);
            }
        }
        catch (Exception exception)
        {
            // A fault reporter that throws cannot safely report its own failure. The original
            // fault remains available through StopWatchingAsync. Discarded through the variable
            // rather than an empty body, which is this repository's idiom for a deliberate
            // swallow and what keeps RCS1075 honest here.
            _ = exception;
        }
    }
}
