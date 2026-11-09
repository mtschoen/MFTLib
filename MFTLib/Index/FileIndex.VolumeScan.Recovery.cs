namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>Scans of this volume in a row whose journal catch-up was lost.</summary>
        int _consecutiveLostCatchUps;

        /// <summary>Automatic catch-up retries exhausted; only a successful publication clears it.</summary>
        bool _recoveryStopped;

        /// <summary>
        ///     Set while a scan operation of a watched root retries after a lost catch-up, which is
        ///     what makes the root read <see cref="WatchState.Recovering" />.
        /// </summary>
        bool _retryingLostCatchUp;

        /// <summary>The lost catch-up a retrying scan operation is rescanning for, while it retries.</summary>
        JournalCatchUpLostException? _retriedLostCatchUp;

        /// <summary>
        ///     The completion of every recovery that has been queued and has not ended, including one
        ///     whose ticket a stop, start, or rescan has since cleared. Guarded by
        ///     <see cref="StateLock" />; disposal waits for all of them.
        /// </summary>
        readonly HashSet<Task> _recoveryCompletions = [];

        /// <summary>
        ///     Clears the root's recovery ticket, which a manual rescan, a consumer start, a stop, or
        ///     disposal supersedes. A recovery already past its revalidation keeps running, and its
        ///     failure is then no longer reported, since the ticket is not the root's any more. The
        ///     caller holds <see cref="StateLock" />.
        /// </summary>
        void ClearRecoveryLocked()
        {
            Recovery?.MarkSuperseded();
            Recovery = null;
            _recoveryState = RecoveryState.None;
        }

        /// <summary>
        ///     Stores a recovery ticket for a root whose current instance just faulted with a
        ///     <see cref="WatchFaultKind.Volume" /> or <see cref="WatchFaultKind.Apply" /> fault, which
        ///     makes the root read <see cref="WatchState.Recovering" />, and returns it for
        ///     <see cref="StartRecovery" /> once the fault has been raised. Returns null when the
        ///     instance is no longer the root's current faulted watch, its watch is no longer
        ///     requested, or the index is being disposed.
        /// </summary>
        RecoveryTicket? QueueRecovery(WatchInstance instance, WatchFault fault)
        {
            lock (StateLock)
            {
                if (IsClosing || !AnyRequestLocked || !IsCurrentWatchOverItsBlockLocked(instance) ||
                    instance.State != WatchInstanceState.Faulted)
                {
                    return null;
                }

                var ticket = new RecoveryTicket(instance, instance.ArmedBlock, ClosingToken);
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
        ///     The pauses before each retry of a recovery rescan that failed because its volume is
        ///     absent: about 30 seconds in all, for a volume that is replugged or remounted.
        /// </summary>
        internal static readonly TimeSpan[] RecoveryRetryDelays =
        [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
        ];

        /// <summary>
        ///     One recovery: takes the root's lifecycle gate, revalidates the ticket, and runs the
        ///     rescan body with the gate held. A rescan that fails while the volume is absent is
        ///     retried after each of <see cref="RecoveryRetryDelays" />, with the gate released
        ///     during the wait and the root still <see cref="WatchState.Recovering" />; a stop,
        ///     start or rescan, or disposal, ends the wait at once. A recovery that fails any other
        ///     way, or fails its last retry, while its ticket is still the root's raises
        ///     <see cref="WatchFaultKind.Recovery" /> after releasing the gate, and the root stays
        ///     <see cref="WatchState.Faulted" /> until the consumer rescans or starts it. Never
        ///     throws, and always completes its ticket.
        /// </summary>
        async Task RunRecoveryAsync(RecoveryTicket ticket)
        {
            Exception? failure = null;
            try
            {
                if (Hooks.RecoveryBeforeLifecycleGate is { } beforeGate)
                {
                    await beforeGate(Root, ticket.Cancellation.Token).ConfigureAwait(false);
                }

                for (var retry = 0; ; retry++)
                {
                    failure = await RecoverOnceAsync(ticket).ConfigureAwait(false);
                    if (failure is null || retry == RecoveryRetryDelays.Length || !ticket.RescanProducerFailed ||
                        !IsVolumeAbsent())
                    {
                        break;
                    }

                    if (!await WaitForRecoveryRetryAsync(ticket, RecoveryRetryDelays[retry]).ConfigureAwait(false))
                    {
                        failure = null;
                        break;
                    }
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

        /// <summary>One recovery attempt under the root's lifecycle gate; null when it succeeded or was superseded.</summary>
        async Task<Exception?> RecoverOnceAsync(RecoveryTicket ticket)
        {
            await LifecycleGate.WaitAsync(ticket.Cancellation.Token).ConfigureAwait(false);
            try
            {
                ticket.RescanProducerFailed = false;
                return RevalidateRecovery(ticket)
                    ? await RecoverWithGateHeldAsync(ticket).ConfigureAwait(false)
                    : null;
            }
            finally
            {
                LifecycleGate.Release();
            }
        }

        /// <summary>
        ///     True when the volume under the root reads as not ready now, which is what a rescan
        ///     that produced no block is retried for. Reads the facts the open read; a dump has no
        ///     volume to lose.
        /// </summary>
        bool IsVolumeAbsent() =>
            !Resolved.IsDump && !RootResolution.ReadVolumeFacts(Options, Root, Resolved.RootDirectory).IsReady;

        /// <summary>
        ///     Waits out one retry pause on the index's clock. Returns false when disposal or a
        ///     stop, start or rescan of the root ended the recovery first.
        /// </summary>
        async Task<bool> WaitForRecoveryRetryAsync(RecoveryTicket ticket, TimeSpan delay)
        {
            using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ticket.Cancellation.Token);
            var elapsed = Task.Delay(delay, Options.TimeProvider, timerCancellation.Token);
            try
            {
                var finished = await Task.WhenAny(elapsed, ticket.Superseded).ConfigureAwait(false);
                return ReferenceEquals(finished, elapsed) && elapsed.IsCompletedSuccessfully;
            }
            finally
            {
                await timerCancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     The revalidation a recovery runs once it holds the root's lifecycle gate: the ticket is
        ///     still the root's, its instance is still the root's current faulted watch, the block
        ///     that instance was armed from is still published, the watch is still requested, and the
        ///     index is not being disposed. A recovery that fails any of these ends without scanning.
        /// </summary>
        bool RevalidateRecovery(RecoveryTicket ticket)
        {
            lock (StateLock)
            {
                return !IsClosing && AnyRequestLocked && ReferenceEquals(Recovery, ticket) &&
                       ReferenceEquals(Current, ticket.FailedInstance) &&
                       ticket.FailedInstance.State == WatchInstanceState.Faulted &&
                       ReferenceEquals(FindWatchableRootBlockLocked(), ticket.FailedBlock);
            }
        }

        /// <summary>
        ///     The rescan body with the gate already held, as a recovery runs it: it keeps the root's
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
            catch (Exception) when (ticket.Cancellation.IsCancellationRequested || IsClosing)
            {
                return null;
            }
            catch (Exception failure)
            {
                return failure;
            }
        }

        /// <summary>
        ///     Clears the ticket when it is still the root's, raises <see cref="WatchFaultKind.Recovery" />
        ///     for a failure only in that case, since a superseded recovery's failure describes no watch
        ///     the root still has, then completes the ticket, and only then stops counting it among
        ///     the recoveries disposal waits for. A root whose recovery restarted its watch
        ///     keeps <see cref="RecoveryState.RecoveredAwaitingCatchUp" /> until that watch catches up.
        /// </summary>
        void EndRecovery(RecoveryTicket ticket, Exception? failure)
        {
            WatchFault? reported = null;
            IndexView[] recipients = [];
            lock (StateLock)
            {
                if (ReferenceEquals(Recovery, ticket))
                {
                    Recovery = null;
                    if (failure is not null || _recoveryState == RecoveryState.Recovering)
                    {
                        _recoveryState = RecoveryState.None;
                    }

                    reported = failure is null ? null : new WatchFault(WatchFaultKind.Recovery, Volume, failure);
                    if (reported is not null)
                    {
                        recipients = CaptureFaultLocked(reported);
                    }
                    NoteWatchStateLocked(reported);
                }
            }

            try
            {
                RaiseWatchStateChanged();
                if (reported is not null)
                {
                    RaiseWatchFaulted(reported, recipients);
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
