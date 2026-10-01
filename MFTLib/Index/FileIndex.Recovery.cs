using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The completion of every recovery that has been queued and has not ended, including one
    ///     whose ticket a stop, start, or rescan has since cleared. Guarded by
    ///     <see cref="_stateLock" />; disposal waits for all of them.
    /// </summary>
    readonly HashSet<Task> _recoveryCompletions = [];

    /// <summary>
    ///     A test seam: awaited with the drive letter and the recovery's token, which the index's
    ///     disposal cancels, by a queued recovery before it waits for the drive's lifecycle gate, so
    ///     a test can hold a recovery that has not started yet.
    /// </summary>
    internal Func<char, CancellationToken, Task>? RecoveryBeforeLifecycleGateForTest { get; set; }

    /// <summary>
    ///     A test seam: when set, a recovery calls it as <c>wrapper(complete)</c> around exactly the
    ///     step that completes its ticket, so a test can observe what runs on that stack.
    /// </summary>
    internal Action<Action>? RecoveryCompletionWrapperForTest { get; set; }

    /// <summary>A test seam: the completion of the drive's current recovery ticket, when it has one.</summary>
    internal bool TryGetRecoveryCompletionForTest(char driveLetter, [NotNullWhen(true)] out Task? completion)
    {
        var runtime = GetDriveRuntime(driveLetter);
        lock (_stateLock)
        {
            completion = runtime.Recovery?.Completion;
            return completion is not null;
        }
    }

    /// <summary>
    ///     One queued recovery of a drive (spec 2.6.5): the faulted instance and the block it was
    ///     armed from, which the recovery revalidates once it holds the drive's lifecycle gate, and
    ///     a cancellation source that the index's disposal cancels. Its completion never faults and
    ///     completes its continuations asynchronously, so no waiter's code runs on the recovery's
    ///     stack.
    /// </summary>
    sealed class RecoveryTicket
    {
        readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _disposalLink;

        public RecoveryTicket(WatchInstance failedInstance, DriveBlock failedBlock, CancellationToken disposalToken)
        {
            FailedInstance = failedInstance;
            FailedBlock = failedBlock;

            // Linked by hand, as WatchInstance links its start: the link is removed when the
            // recovery ends without disposing the source, which disposal may still be cancelling.
            _disposalLink = disposalToken.UnsafeRegister(
                static state => (state as CancellationTokenSource)?.Cancel(), Cancellation);
        }

        public WatchInstance FailedInstance { get; }

        public DriveBlock FailedBlock { get; }

        /// <summary>Cancelled by the index's disposal; never disposed.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion => _completion.Task;

        public void Complete()
        {
            _disposalLink.Dispose();
            _completion.TrySetResult();
        }
    }

    /// <summary>
    ///     Stores a recovery ticket for a drive whose current instance just faulted with a
    ///     <see cref="WatchFaultKind.Drive" /> or <see cref="WatchFaultKind.Apply" /> fault, which
    ///     makes the drive read <see cref="WatchCatchUpState.Recovering" />, and returns it for
    ///     <see cref="StartRecovery" /> once the fault has been raised. Returns null when the
    ///     instance is no longer the drive's current faulted watch, its watch is no longer
    ///     requested, or the index is being disposed.
    /// </summary>
    RecoveryTicket? QueueRecovery(DriveRuntime runtime, WatchInstance instance, WatchFault fault)
    {
        lock (_stateLock)
        {
            if (_disposed || !runtime.WatchRequested || !IsCurrentWatchOverItsBlockLocked(runtime, instance) ||
                instance.State != WatchInstanceState.Faulted)
            {
                return null;
            }

            var ticket = new RecoveryTicket(instance, instance.ArmedBlock, DisposalToken);
            runtime.Recovery = ticket;
            runtime.RecoveryState = RecoveryState.Recovering;
            _recoveryCompletions.Add(ticket.Completion);
            NoteWatchStateLocked(runtime, fault);
            return ticket;
        }
    }

    /// <summary>Runs the recovery on the thread pool, off the pump that queued it.</summary>
    void StartRecovery(DriveRuntime runtime, RecoveryTicket ticket)
    {
        _ = Task.Run(() => RunRecoveryAsync(runtime, ticket));
    }

    /// <summary>
    ///     One recovery: takes the drive's lifecycle gate, revalidates the ticket, and runs the
    ///     rescan body with the gate held. A recovery that fails while its ticket is still the
    ///     drive's raises <see cref="WatchFaultKind.Recovery" /> after releasing the gate, and the
    ///     drive stays <see cref="WatchCatchUpState.Faulted" /> until the consumer rescans or starts
    ///     it. Never throws, and always completes its ticket.
    /// </summary>
    async Task RunRecoveryAsync(DriveRuntime runtime, RecoveryTicket ticket)
    {
        Exception? failure = null;
        try
        {
            if (RecoveryBeforeLifecycleGateForTest is { } beforeGate)
            {
                await beforeGate(runtime.DriveLetter, ticket.Cancellation.Token).ConfigureAwait(false);
            }

            await runtime.LifecycleGate.WaitAsync(ticket.Cancellation.Token).ConfigureAwait(false);
            try
            {
                if (RevalidateRecovery(runtime, ticket))
                {
                    failure = await RecoverWithGateHeldAsync(runtime, ticket).ConfigureAwait(false);
                }
            }
            finally
            {
                runtime.LifecycleGate.Release();
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
            EndRecovery(runtime, ticket, failure);
        }
    }

    /// <summary>
    ///     The revalidation a recovery runs once it holds the drive's lifecycle gate: the ticket is
    ///     still the drive's, its instance is still the drive's current faulted watch, the block
    ///     that instance was armed from is still published, the watch is still requested, and the
    ///     index is not being disposed. A recovery that fails any of these ends without scanning.
    /// </summary>
    bool RevalidateRecovery(DriveRuntime runtime, RecoveryTicket ticket)
    {
        lock (_stateLock)
        {
            return !_disposed && runtime.WatchRequested && ReferenceEquals(runtime.Recovery, ticket) &&
                   ReferenceEquals(runtime.Current, ticket.FailedInstance) &&
                   ticket.FailedInstance.State == WatchInstanceState.Faulted &&
                   ReferenceEquals(FindWatchableDriveBlockLocked(runtime.DriveLetter), ticket.FailedBlock);
        }
    }

    /// <summary>
    ///     The rescan body with the gate already held, as a recovery runs it: it keeps the drive's
    ///     checkpoint-loss report, re-checks the watch request before each retry of a lost catch-up,
    ///     and restarts the watch only while it is still requested. Returns the failure to report,
    ///     or null when there is none: a success, a cancellation by disposal, or a lost catch-up at
    ///     <see cref="LostCatchUpRecoveryLimit" />, which the scan operation has already raised.
    /// </summary>
    async Task<Exception?> RecoverWithGateHeldAsync(DriveRuntime runtime, RecoveryTicket ticket)
    {
        try
        {
            await RescanWithGateHeldAsync(runtime, _driveConfigurations[runtime.DriveLetter], ticket,
                ticket.Cancellation.Token).ConfigureAwait(false);
            return null;
        }
        catch (JournalCatchUpLostException)
        {
            return null;
        }
        catch (Exception) when (ticket.Cancellation.IsCancellationRequested || _disposed)
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
    void EndRecovery(DriveRuntime runtime, RecoveryTicket ticket, Exception? failure)
    {
        WatchFault? reported = null;
        lock (_stateLock)
        {
            if (ReferenceEquals(runtime.Recovery, ticket))
            {
                runtime.Recovery = null;
                if (failure is not null || runtime.RecoveryState == RecoveryState.Recovering)
                {
                    runtime.RecoveryState = RecoveryState.None;
                }

                reported = failure is null ? null : new WatchFault(WatchFaultKind.Recovery, runtime.DriveLetter, failure);
                NoteWatchStateLocked(runtime, reported);
            }
        }

        try
        {
            RaiseWatchStateChanged(runtime);
            if (reported is not null)
            {
                RaiseWatchFaulted(reported);
            }
        }
        finally
        {
            if (RecoveryCompletionWrapperForTest is { } wrapper)
            {
                wrapper(ticket.Complete);
            }
            else
            {
                ticket.Complete();
            }

            lock (_stateLock)
            {
                _recoveryCompletions.Remove(ticket.Completion);
            }
        }
    }
}
