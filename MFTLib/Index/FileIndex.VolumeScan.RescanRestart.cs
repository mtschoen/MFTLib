namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal readonly record struct WatchHandoff(WatchInstance? Retired, Task Drained)
    {
        /// <summary>Called after publication, with only the root's lifecycle gate held.</summary>
        public Task DrainAsync()
        {
            Retired?.RequestStop();
            return Drained;
        }
    }

    internal sealed partial class VolumeScan
    {
        /// <summary>
        /// Runs one admitted rescan under this root's lifecycle gate. Production keeps the current
        /// watch; publication commits its replacement and hands off teardown before an eligible restart.
        /// See docs/watch-lifetime.md for publication, stop-wins and cancellation ordering.
        /// </summary>
        async Task RescanWithGateHeldAsync(RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            if (recovery is null)
            {
                lock (StateLock)
                {
                    ClearRecoveryLocked();
                    NoteWatchStateLocked();
                }

                RaiseWatchStateChanged();
            }

            ScanAttempt outcome;
            try
            {
                outcome = await RunScanOperationAsync(recovery, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not JournalCatchUpLostException)
            {
                _ = RefuseWatchOverUnresumableBlock();
                throw;
            }

            if (outcome.Published)
            {
                ClearRefusedStartAfterReplacement();
                await RestartRequestedWatchAsync(recovery).ConfigureAwait(false);
                return;
            }

            _ = RefuseWatchOverUnresumableBlock();
            throw new InvalidOperationException(
                $"Root {Root} was not rescanned: {outcome.ProducerFailureMessage}", outcome.ProducerFailure);
        }

        /// <summary>
        ///     True when the root's published block cannot be resumed; a watch that is still requested
        ///     then records the refusal <see cref="StartWatchingAsync" /> would record, without a start.
        /// </summary>
        bool RefuseWatchOverUnresumableBlock()
        {
            try
            {
                lock (StateLock)
                {
                    if (FindWatchableRootBlockLocked() is not { } rootBlock ||
                        _unresumableCheckpoint is not { } reason)
                    {
                        return false;
                    }

                    if (AnyRequestLocked)
                    {
                        _ = RecordUnresumableCheckpointWatchFailureLocked(rootBlock, reason);
                        NoteWatchStateLocked();
                    }

                    return true;
                }
            }
            finally
            {
                RaiseWatchStateChanged();
            }
        }

        /// <summary>
        ///     Captures the old pump and any predecessor's teardown while retiring a healthy current
        ///     watch in the publication's state-lock section. A faulted current watch stays until
        ///     restart registration supersedes it. A retired watch's subscriber fault stays on the
        ///     runtime for stop to take even after the pump drains. The returned drains never fault.
        /// </summary>
        WatchHandoff RetireWatchAtCommitLocked()
        {
            var predecessorDrain = _retiring?.Drained ?? Task.CompletedTask;
            var currentDrain = Current?.Drained ?? Task.CompletedTask;
            var retired = Current is { State: WatchInstanceState.Starting or WatchInstanceState.Running }
                ? RetireCurrentLocked()
                : null;
            return new WatchHandoff(retired, Task.WhenAll(predecessorDrain, currentDrain));
        }

        /// <summary>
        ///     A replacement block has a fresh cursor, so a start refused or failed over the old block
        ///     describes the replaced block. A faulted current watch is left for the restart that
        ///     follows, which supersedes it.
        /// </summary>
        void ClearRefusedStartAfterReplacement()
        {
            lock (StateLock)
            {
                if (Current is not null)
                {
                    return;
                }

                _refusedStartFault = null;
                _watchFailureMessage = null;
                NoteWatchStateLocked();
            }

            RaiseWatchStateChanged();
        }

        /// <summary>
        ///     Starts the root's watch again, with the lifecycle gate the rescan holds, when it is
        ///     still requested and the index is not being disposed. Not bounded by the rescan's token;
        ///     see <see cref="RescanAsync" />. A recovery's restart names its ticket, which marks the
        ///     restarted watch as awaiting its first catch-up. Disposal cancels the operation at
        ///     this checkpoint even when the watch is not requested; a recovery drops it quietly.
        /// </summary>
        Task RestartRequestedWatchAsync(RecoveryTicket? recovery)
        {
            Hooks.BeforeRestartDecision?.Invoke(Root);
            bool requested;
            lock (StateLock)
            {
                ThrowIfClosing(CancellationToken.None);

                requested = AnyRequestLocked && !IsClosing;
            }

            if (!requested)
            {
                return Task.CompletedTask;
            }

            Hooks.RestartRequested?.Invoke(Root);
            return StartWatchingWithGateHeldAsync(restart: true, recovery, CancellationToken.None);
        }

        /// <summary>
        ///     Retains a failed rescan restart as a faulted instance, so stop takes its outstanding
        ///     fault once. A stop or disposal that already retired it wins and records no new fault.
        ///     The scan has committed; this failure belongs only to the watch.
        /// </summary>
        bool RecordRescanRestartFailure(WatchInstance instance, Exception startFailure)
        {
            var failure = new InvalidOperationException(
                $"A rescan replaced root {Root}'s block and the watch could not be started on it.",
                startFailure);
            var fault = new WatchFault(WatchFaultKind.RescanRestart, Volume, failure);
            IndexView[] recipients;
            lock (StateLock)
            {
                if (IsClosing || !ReferenceEquals(Current, instance))
                {
                    return false;
                }

                instance.State = WatchInstanceState.Faulted;
                instance.CatchUp.Fault(failure);
                _watchFailureMessage = failure.Message;
                recipients = CaptureFaultLocked(fault);
                NoteWatchStateLocked(fault);
            }

            CompleteInstanceDrain(instance);
            RaiseWatchFaulted(fault, recipients);
            return true;
        }
    }
}
