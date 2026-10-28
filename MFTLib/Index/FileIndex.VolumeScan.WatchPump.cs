namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     One root's pump: reads the root's handle until it is stopped or faults, applies each
        ///     batch, and records a fault only while its instance is still the root's current one. On
        ///     every exit it is the handle's only disposer, and it completes the instance's
        ///     <see cref="WatchInstance.Drained" /> last.
        /// </summary>
        async Task PumpAsync(WatchInstance instance, IIndexVolumeWatch handle)
        {
            try
            {
                if (await ReadWatchAsync(instance, handle).ConfigureAwait(false) is { } fault)
                {
                    RecordPumpFault(instance, fault);
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
                    // The root's watch is over either way, and a stop or disposal waiting on this
                    // teardown has no one to hand the failure to. Discarded through the variable
                    // rather than an empty body, which is this repository's idiom for a deliberate
                    // swallow and what keeps RCS1075 honest here.
                    _ = exception;
                }
                finally
                {
                    CompleteInstanceDrain(instance);
                }
            }
        }

        /// <summary>
        ///     Reads the handle to its end and classifies that end: null for a stop, otherwise the
        ///     fault that ended the watch.
        /// </summary>
        async Task<WatchFault?> ReadWatchAsync(WatchInstance instance, IIndexVolumeWatch handle)
        {
            var volume = Volume;
            var stopToken = instance.PumpStop.Token;
            try
            {
                await foreach (var item in handle.ReadAsync(stopToken).ConfigureAwait(false))
                {
                    if (item is JournalBatch batch)
                    {
                        if (await ApplyWatchBatchAsync(instance, batch, stopToken).ConfigureAwait(false) is
                            { } applyFailure)
                        {
                            return new WatchFault(WatchFaultKind.Apply, volume, applyFailure);
                        }
                    }
                    else if (item is VolumeCaughtUp)
                    {
                        CompleteWatchCatchUp(instance);
                    }
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return null;
            }
            catch (VolumeWatchFaultException exception)
            {
                return new WatchFault(WatchFaultKind.Volume, volume, exception);
            }
            catch (Exception exception)
            {
                return new WatchFault(WatchFaultKind.Channel, volume, exception);
            }

            return stopToken.IsCancellationRequested
                ? null
                : new WatchFault(WatchFaultKind.Channel, volume,
                    new InvalidOperationException($"The watch for root {volume} ended without being stopped."));
        }

        /// <summary>
        ///     Applies one batch, then raises <see cref="Changed" /> with no gate or lock held. Returns
        ///     the apply failure, which ends the root's watch; a subscriber failure does not. A stop
        ///     that arrives while the batch waits for the write gate propagates as cancellation, which
        ///     the pump classifies as a stop.
        /// </summary>
        async Task<Exception?> ApplyWatchBatchAsync(WatchInstance instance, JournalBatch batch,
            CancellationToken stopToken)
        {
            IReadOnlyList<RoutedChanges> changes;
            try
            {
                changes = await ApplyJournalEntriesCoreAsync(instance, batch.Entries, batch.JournalIdentifier,
                    batch.NextUsn, stopToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException &&
                                                stopToken.IsCancellationRequested))
            {
                return exception;
            }

            Hooks.BeforeWatchChanged?.Invoke(Root);
            DeliverChanges(changes, instance);

            return null;
        }

        /// <summary>
        ///     Announces a subscriber fault once per instance and keeps the first as the instance's
        ///     outstanding fault, unless one is already held. The root keeps watching. A delayed fault
        ///     from a watch retired during rescan publication is also retained for stop handoff.
        /// </summary>
        void AnnounceSubscriberFault(WatchInstance instance, IndexView view, Exception exception)
        {
            bool first;
            lock (StateLock)
            {
                first = !instance.SubscriberFaultAnnounced.Contains(view) &&
                        (ReferenceEquals(Current, instance) || ReferenceEquals(_retiring, instance) ||
                         instance.State == WatchInstanceState.Retiring);
                if (first)
                {
                    instance.SubscriberFaultAnnounced.Add(view);
                    RecordViewFaultLocked(view, instance, exception);
                }
            }

            if (first)
            {
                view.RaiseWatchFaulted(new WatchFault(WatchFaultKind.Subscriber, Volume, exception));
            }
        }

        void CompleteWatchCatchUp(WatchInstance instance)
        {
            lock (StateLock)
            {
                if (ReferenceEquals(Current, instance) && instance.State == WatchInstanceState.Running)
                {
                    instance.CatchUp.Complete();
                    if (_recoveryState == RecoveryState.RecoveredAwaitingCatchUp)
                    {
                        _recoveryState = RecoveryState.None;
                    }

                    NoteWatchStateLocked();
                }
            }

            RaiseWatchStateChanged();
        }

        /// <summary>
        ///     Records the fault that ended a watch, only while its instance is still the root's
        ///     current one and the index is not being disposed: the failure message, the outstanding
        ///     fault, the faulted slot, and the faulted state. Then the checkpoint-loss check, then,
        ///     for a <see cref="WatchFaultKind.Volume" /> or <see cref="WatchFaultKind.Apply" /> fault,
        ///     the recovery ticket that makes the root read <see cref="WatchState.Recovering" />,
        ///     then the catch-up waiter's fault, then <see cref="WatchFaulted" />, so a waiter or a
        ///     handler that reads <see cref="Status" /> already sees why the root stopped and that it
        ///     is recovering, and only then the recovery itself.
        ///     A <see cref="WatchFaultKind.Channel" /> fault never recovers. A watch a recovery
        ///     restarted that faults before it first catches up queues no further recovery: its
        ///     <see cref="WatchFaultKind.Volume" /> or <see cref="WatchFaultKind.Apply" /> fault is
        ///     raised as <see cref="WatchFaultKind.Recovery" />.
        /// </summary>
        void RecordPumpFault(WatchInstance instance, WatchFault fault)
        {
            var recovers = fault.Kind is WatchFaultKind.Volume or WatchFaultKind.Apply;
            var recoveryFailed = false;
            var reported = fault;
            TaskCompletionSource? faultedWaiter = null;

            void Settle()
            {
                lock (StateLock)
                {
                    if (IsClosing || !ReferenceEquals(Current, instance) ||
                        instance.State != WatchInstanceState.Running)
                    {
                        return;
                    }

                    instance.State = WatchInstanceState.Faulted;
                    _watchFailureMessage = fault.Exception.Message;
                    faultedWaiter = instance.CatchUp.MarkFaulted(fault.Exception);
                    recoveryFailed = _recoveryState == RecoveryState.RecoveredAwaitingCatchUp;
                    if (recoveryFailed)
                    {
                        _recoveryState = RecoveryState.None;
                        if (recovers)
                        {
                            reported = fault with { Kind = WatchFaultKind.Recovery };
                        }
                    }

                    CaptureFaultLocked(reported);
                    NoteWatchStateLocked(reported);
                }
            }

            if (Hooks.PumpFaultSettlementWrapper is { } wrapper)
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
                RecordCheckpointLossForFaultedRoot(instance);
                recovery = recovers && !recoveryFailed ? QueueRecovery(instance, fault) : null;
            }
            finally
            {
                WatchCatchUpSlot.ReleaseFault(faultedWaiter, fault.Exception);

                // Delivers what the settlement noted even when the checkpoint check threw.
                RaiseWatchStateChanged();
            }

            RaiseWatchFaulted(reported);
            if (recovery is not null)
            {
                StartRecovery(recovery);
            }
        }
    }
}
