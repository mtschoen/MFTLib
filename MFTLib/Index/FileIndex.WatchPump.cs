namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     A test seam: when set, the pump calls it as <c>wrapper(settleFault)</c> around exactly
    ///     the step that records a drive's fault and faults that drive's catch-up slot, before
    ///     <see cref="WatchFaulted" /> is raised, so a test can observe what runs on the settling
    ///     stack.
    /// </summary>
    internal Action<Action>? PumpFaultSettlementWrapperForTest { get; set; }

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
    ///     outstanding fault, unless one is already held. The drive keeps watching.
    /// </summary>
    void AnnounceSubscriberFault(DriveRuntime runtime, WatchInstance instance, Exception exception)
    {
        bool first;
        lock (_stateLock)
        {
            first = ReferenceEquals(runtime.Current, instance) && !instance.SubscriberFaultAnnounced;
            if (first)
            {
                instance.SubscriberFaultAnnounced = true;
                instance.OutstandingFault ??= exception;
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
            }
        }
    }

    /// <summary>
    ///     Records the fault that ended a watch, only while its instance is still the drive's
    ///     current one and the index is not being disposed: the failure message, the outstanding
    ///     fault, the faulted slot, and the faulted state. Then the checkpoint-loss check, then
    ///     <see cref="WatchFaulted" />, so a handler that reads <see cref="Drives" /> already sees
    ///     why the drive stopped.
    /// </summary>
    void RecordPumpFault(DriveRuntime runtime, WatchInstance instance, WatchFault fault)
    {
        var recorded = false;

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
                instance.CatchUp.Fault(fault.Exception);
                recorded = true;
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

        if (!recorded)
        {
            return;
        }

        RecordCheckpointLossForFaultedDrive(runtime, instance);
        RaiseWatchFaulted(fault);
    }

    void RaiseWatchFaulted(WatchFault fault)
    {
        try
        {
            WatchFaulted?.Invoke(fault);
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
