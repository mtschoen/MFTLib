namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     The completion of every recovery that has been queued and has not ended, including one
        ///     whose ticket a stop, start, or rescan has since cleared. Guarded by
        ///     <see cref="StateLock" />; disposal waits for all of them.
        /// </summary>
        readonly HashSet<Task> _recoveryCompletions = [];

        /// <summary>
        ///     Clears the drive's recovery ticket, which a manual rescan, a consumer start, a stop, or
        ///     disposal supersedes. A recovery already past its revalidation keeps running, and its
        ///     failure is then no longer reported, since the ticket is not the drive's any more. The
        ///     caller holds <see cref="StateLock" />.
        /// </summary>
        void ClearRecoveryLocked()
        {
            Recovery = null;
            _recoveryState = RecoveryState.None;
        }

        /// <summary>
        ///     Stores a recovery ticket for a drive whose current instance just faulted with a
        ///     <see cref="WatchFaultKind.Drive" /> or <see cref="WatchFaultKind.Apply" /> fault, which
        ///     makes the drive read <see cref="WatchState.Recovering" />, and returns it for
        ///     <see cref="StartRecovery" /> once the fault has been raised. Returns null when the
        ///     instance is no longer the drive's current faulted watch, its watch is no longer
        ///     requested, or the index is being disposed.
        /// </summary>
        RecoveryTicket? QueueRecovery(WatchInstance instance, WatchFault fault)
        {
            lock (StateLock)
            {
                if (IsDisposed || !_watchRequested || !IsCurrentWatchOverItsBlockLocked(instance) ||
                    instance.State != WatchInstanceState.Faulted)
                {
                    return null;
                }

                var ticket = new RecoveryTicket(instance, instance.ArmedBlock, DisposalToken);
                Recovery = ticket;
                _recoveryState = RecoveryState.Recovering;
                _recoveryCompletions.Add(ticket.Completion);
                NoteWatchStateLocked(fault);
                return ticket;
            }
        }

        /// <summary>Runs the recovery on the thread pool, off the pump that queued it.</summary>
        void StartRecovery(RecoveryTicket ticket)
        {
            // Never the disposal token: the recovery is the only completer of the ticket that
            // disposal awaits, so it must start even after disposal has begun.
            _ = Task.Run(() => RunRecoveryAsync(ticket), CancellationToken.None);
        }

        /// <summary>
        ///     One recovery: takes the drive's lifecycle gate, revalidates the ticket, and runs the
        ///     rescan body with the gate held. A recovery that fails while its ticket is still the
        ///     drive's raises <see cref="WatchFaultKind.Recovery" /> after releasing the gate, and the
        ///     drive stays <see cref="WatchState.Faulted" /> until the consumer rescans or starts
        ///     it. Never throws, and always completes its ticket.
        /// </summary>
        async Task RunRecoveryAsync(RecoveryTicket ticket)
        {
            Exception? failure = null;
            try
            {
                if (Hooks.RecoveryBeforeLifecycleGate is { } beforeGate)
                {
                    await beforeGate(Drive, ticket.Cancellation.Token).ConfigureAwait(false);
                }

                await LifecycleGate.WaitAsync(ticket.Cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (RevalidateRecovery(ticket))
                    {
                        failure = await RecoverWithGateHeldAsync(ticket).ConfigureAwait(false);
                    }
                }
                finally
                {
                    LifecycleGate.Release();
                }
            }
            catch (OperationCanceledException) when (ticket.Cancellation.IsCancellationRequested)
            {
                // Disposal cancelled the recovery before it held the gate; there is nothing to
                // report, since disposal never reports a watch's fault.
                failure = null;
            }
            finally
            {
                EndRecovery(ticket, failure);
            }
        }

        /// <summary>
        ///     The revalidation a recovery runs once it holds the drive's lifecycle gate: the ticket is
        ///     still the drive's, its instance is still the drive's current faulted watch, the block
        ///     that instance was armed from is still published, the watch is still requested, and the
        ///     index is not being disposed. A recovery that fails any of these ends without scanning.
        /// </summary>
        bool RevalidateRecovery(RecoveryTicket ticket)
        {
            lock (StateLock)
            {
                return !IsDisposed && _watchRequested && ReferenceEquals(Recovery, ticket) &&
                       ReferenceEquals(Current, ticket.FailedInstance) &&
                       ticket.FailedInstance.State == WatchInstanceState.Faulted &&
                       ReferenceEquals(FindWatchableDriveBlockLocked(), ticket.FailedBlock);
            }
        }

        /// <summary>
        ///     The rescan body with the gate already held, as a recovery runs it: it keeps the drive's
        ///     checkpoint-loss report, re-checks the watch request before each retry of a lost catch-up,
        ///     and restarts the watch only while it is still requested. Returns the failure to report,
        ///     or null when there is none: a success, a cancellation by disposal, or a lost catch-up at
        ///     <see cref="LostCatchUpRecoveryLimit" />, which the scan operation has already raised.
        /// </summary>
        async Task<Exception?> RecoverWithGateHeldAsync(RecoveryTicket ticket)
        {
            try
            {
                await RescanWithGateHeldAsync(ticket, ticket.Cancellation.Token).ConfigureAwait(false);
                return null;
            }
            catch (JournalCatchUpLostException)
            {
                return null;
            }
            catch (Exception) when (ticket.Cancellation.IsCancellationRequested || IsDisposed)
            {
                return null;
            }
            catch (Exception failure)
            {
                return failure;
            }
        }

        /// <summary>
        ///     Clears the ticket when it is still the drive's, raises <see cref="WatchFaultKind.Recovery" />
        ///     for a failure only in that case, since a superseded recovery's failure describes no watch
        ///     the drive still has, then completes the ticket, and only then stops counting it among
        ///     the recoveries disposal waits for. A drive whose recovery restarted its watch
        ///     keeps <see cref="RecoveryState.RecoveredAwaitingCatchUp" /> until that watch catches up.
        /// </summary>
        void EndRecovery(RecoveryTicket ticket, Exception? failure)
        {
            WatchFault? reported = null;
            lock (StateLock)
            {
                if (ReferenceEquals(Recovery, ticket))
                {
                    Recovery = null;
                    if (failure is not null || _recoveryState == RecoveryState.Recovering)
                    {
                        _recoveryState = RecoveryState.None;
                    }

                    reported = failure is null ? null : new WatchFault(WatchFaultKind.Recovery, Drive, failure);
                    NoteWatchStateLocked(reported);
                }
            }

            try
            {
                RaiseWatchStateChanged();
                if (reported is not null)
                {
                    RaiseWatchFaulted(reported);
                }
            }
            finally
            {
                if (Hooks.RecoveryCompletionWrapper is { } wrapper)
                {
                    wrapper(ticket.Complete);
                }
                else
                {
                    ticket.Complete();
                }

                lock (StateLock)
                {
                    _recoveryCompletions.Remove(ticket.Completion);
                }
            }
        }
    }
}
