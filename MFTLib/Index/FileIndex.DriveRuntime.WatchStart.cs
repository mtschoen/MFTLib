namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>Starts this drive for an admitted consumer request; readiness is handle publication.</summary>
        public Task StartDriveWatchingAsync(CancellationToken cancellationToken)
        {
            if (_index.RejectedTask(nameof(StartWatchingAsync)) is { } rejected)
            {
                return rejected;
            }

            ObjectDisposedException.ThrowIf(IsDisposed, _index);
            return StartConsumerWatchingAsync(cancellationToken);
        }

        async Task StartConsumerWatchingAsync(CancellationToken cancellationToken)
        {
            await LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StartWatchingWithGateHeldAsync(restart: false, recovery: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                LifecycleGate.Release();
            }
        }

        async Task StartWatchingWithGateHeldAsync(bool restart, RecoveryTicket? recovery,
            CancellationToken cancellationToken)
        {
            _index.ThrowIfCancelledByDisposal(cancellationToken);
            var source = WatchSourceOrNull ?? throw new InvalidOperationException(
                MftIndexSource.FormatUnavailable(Drive, MftIndexSource.NoWatchReason));

            (DriveBlock ArmedBlock, Task? PreviousDrain)? preparation;
            try
            {
                preparation = PrepareStart(restart, recovery);
            }
            finally
            {
                RaiseWatchStateChanged();
            }

            if (preparation is not { } prepared)
            {
                return;
            }

            var (armedBlock, previousDrain) = prepared;
            if (previousDrain is not null)
            {
                // The lifecycle gate keeps any other start or rescan out while this waits, and nothing
                // is registered yet, so a retiring pump's last batch can never reach this start's watch.
                await AwaitQueuedAsync(previousDrain, cancellationToken, CancellationToken.None).ConfigureAwait(false);
            }

            if (restart && Hooks.RestartBeforeRegistration is { } beforeRegistration)
            {
                await beforeRegistration(Drive).ConfigureAwait(false);
            }

            var registration = RegisterStartingInstance(armedBlock, restart, recovery);
            RaiseWatchStateChanged();
            if (registration is not { } registered)
            {
                return;
            }

            await RunRegisteredStartAsync(source, registered, restart, recovery, cancellationToken)
                .ConfigureAwait(false);
        }

        (DriveBlock ArmedBlock, Task? PreviousDrain)? PrepareStart(bool restart,
            RecoveryTicket? recovery)
        {
            lock (StateLock)
            {
                try
                {
                    if (restart && !IsRestartStillRequestedLocked(recovery))
                    {
                        return null;
                    }

                    if (recovery is null && Recovery is not null)
                    {
                        ClearRecoveryLocked();
                    }

                    if (FindWatchableDriveBlockLocked() is not { } armedBlock)
                    {
                        throw RefuseStartWithoutWatchableBlockLocked(restart);
                    }

                    if (_unresumableCheckpoint is { } unresumable)
                    {
                        throw RecordUnresumableCheckpointWatchFailureLocked(armedBlock, unresumable);
                    }

                    if (Current is { State: WatchInstanceState.Running })
                    {
                        return null;
                    }

                    var previousDrain = Current is { State: WatchInstanceState.Faulted } faulted
                        ? faulted.Drained
                        : _retiring?.Drained;
                    return (armedBlock, previousDrain);
                }
                finally
                {
                    // Clearing a recovery and every refusal above change the drive's state.
                    NoteWatchStateLocked();
                }
            }
        }

        readonly record struct RegisteredWatchStart(WatchInstance Instance, IndexWatchTarget Target);

        async Task RunRegisteredStartAsync(IIndexWatchSource source, RegisteredWatchStart registration,
            bool restart, RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            var instance = registration.Instance;
            IIndexDriveWatch handle;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    instance.StartCancellation.Token, cancellationToken);
                handle = await source.StartAsync(registration.Target, linked.Token).ConfigureAwait(false) ??
                         throw new InvalidOperationException(
                             $"The watch source returned no handle for drive {Drive}.");
            }
            catch (Exception exception)
            {
                if (restart && recovery is null && RecordRescanRestartFailure(instance, exception))
                {
                    return;
                }

                AbandonFailedStart(instance, exception, recovery, cancellationToken);
                throw;
            }

            var published = TryPublishHandle(instance, handle);
            RaiseWatchStateChanged();
            if (published)
            {
                return;
            }

            // A stop or disposal retired the instance while the source ran. The handle was never
            // published, so no pump owns it and this path is its only disposer.
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                CompleteInstanceDrain(instance);
            }

            throw new OperationCanceledException(
                $"The watch for drive {instance.Drive} (start {instance.Generation}) was stopped while it was starting.");
        }

        RegisteredWatchStart? RegisterStartingInstance(
            DriveBlock armedBlock, bool restart, RecoveryTicket? recovery)
        {
            lock (StateLock)
            {
                _index.ThrowIfCancelledByDisposal(CancellationToken.None);
                if (restart && !IsRestartStillRequestedLocked(recovery))
                {
                    return null;
                }

                // Its pump has ended and its teardown was awaited, so a faulted instance goes
                // straight to drained. A manual rescan keeps its fault until the start settles.
                if (Current is { State: WatchInstanceState.Faulted } faulted)
                {
                    if (restart && recovery is null)
                    {
                        _rescanHandoffFault ??= faulted.OutstandingFault;
                        faulted.OutstandingFault = null;
                    }

                    _ = RetireCurrentLocked();
                }

                var instance = new WatchInstance(Drive, _nextGeneration++, armedBlock,
                    TakeRestartPendingWaiterLocked(), DisposalToken);
                Current = instance;
                if (!restart)
                {
                    _watchRequested = true;
                }

                _refusedStartFault = null;
                _recoveryState = recovery is not null && ReferenceEquals(Recovery, recovery)
                    ? RecoveryState.RecoveredAwaitingCatchUp
                    : RecoveryState.None;
                _watchFailureMessage = null;
                NoteWatchStateLocked();
                return new RegisteredWatchStart(instance, BuildWatchTarget(armedBlock));
            }
        }

        bool IsRestartStillRequestedLocked(RecoveryTicket? recovery) =>
            _watchRequested && (recovery is null || ReferenceEquals(Recovery, recovery));

        void AbandonFailedStart(WatchInstance instance, Exception exception,
            RecoveryTicket? recovery, CancellationToken cancellationToken)
        {
            lock (StateLock)
            {
                if (ReferenceEquals(Current, instance))
                {
                    Current = null;
                    WatchFault? cause = null;
                    if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        _watchRequested = false;
                        instance.CatchUp.Cancel();
                    }
                    else
                    {
                        _refusedStartFault = exception;
                        instance.CatchUp.Fault(exception);
                        _watchFailureMessage = exception.Message;
                        // A stop, a consumer start or disposal that cleared the recovery's ticket also
                        // retired this instance, so a recovery restart still current is still ticketed.
                        if (recovery is not null)
                        {
                            cause = new WatchFault(WatchFaultKind.Recovery, Drive, exception);
                        }
                    }

                    NoteWatchStateLocked(cause);
                }
            }

            RaiseWatchStateChanged();
            CompleteInstanceDrain(instance);
        }

        bool TryPublishHandle(WatchInstance instance, IIndexDriveWatch handle)
        {
            lock (StateLock)
            {
                if (!ReferenceEquals(Current, instance) || instance.State != WatchInstanceState.Starting)
                {
                    return false;
                }

                instance.State = WatchInstanceState.Running;
                NoteWatchStateLocked();
                _rescanHandoffFault = null;
                _retiring?.OutstandingFault = null;

                // Queued rather than run inline, so no source code runs while this lock is held. The
                // pump's own exit path completes the instance's drain, which is what every waiter
                // observes, so the task itself is not kept, and why the pump must start even after
                // disposal has begun: no disposal token is passed.
                _ = Task.Run(() => PumpAsync(instance, handle), CancellationToken.None);
                return true;
            }
        }
    }
}
