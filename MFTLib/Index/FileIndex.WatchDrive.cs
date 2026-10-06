namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Starts the live watch of one MFT-backed drive from the journal cursor persisted in its
    ///     current block header. The drive's <see cref="DriveStatus.WatchFailureMessage" /> is
    ///     cleared and its <see cref="DriveStatus.WatchCatchUp" /> begins at
    ///     <see cref="WatchCatchUpState.CatchingUp" />. Every other drive is untouched. A drive that
    ///     is already watching is left as it is and the call completes. A drive whose previous
    ///     watch faulted is started afresh, which supersedes a queued automatic recovery.
    ///     <para>
    ///         The returned task completes once the source has returned the drive's handle and the
    ///         drive's pump is reading it. A <see cref="StopWatchingAsync(char, CancellationToken)" /> or
    ///         <see cref="DisposeAsync" /> during the start cancels the source's start and fails
    ///         this task with <see cref="OperationCanceledException" />. A source whose start throws
    ///         fails this task with that exception, sets the drive's
    ///         <see cref="DriveStatus.WatchFailureMessage" />, and leaves its
    ///         <see cref="DriveStatus.WatchCatchUp" /> at <see cref="WatchCatchUpState.Faulted" />.
    ///         That failure, and a refusal over an unresumable block, still record the watch as
    ///         requested (<see cref="DriveStatus.WatchRequested" />), so the rescan that replaces
    ///         the block starts it.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="driveLetter" /> is not part of this index.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The drive has no MFT-backed block, <see cref="FileIndexOptions.MftSource" /> is not set or
    ///     cannot watch (<see cref="MftIndexSource.Unavailable" />),
    ///     or the drive's block cursor cannot be resumed, because a cache-only open adopted it
    ///     despite a lost journal checkpoint or because the scan that produced it lost its journal
    ///     catch-up: <see cref="RescanAsync(char, CancellationToken)" /> the drive first.
    /// </exception>
    public Task StartWatchingAsync(char driveLetter, CancellationToken cancellationToken)
    {
        if (RejectedTask(nameof(StartWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        return StartWatchingCoreAsync(GetDriveRuntime(driveLetter), gateHeld: false, recovery: null, cancellationToken);
    }

    /// <summary>
    ///     The start behind <see cref="StartWatchingAsync(char, CancellationToken)" />, and the restart a rescan or a
    ///     recovery (<paramref name="recovery" />) runs while it already holds the drive's lifecycle
    ///     gate (<paramref name="gateHeld" />), which this then never reacquires.
    /// </summary>
    async Task StartWatchingCoreAsync(DriveRuntime runtime, bool gateHeld, RecoveryTicket? recovery,
        CancellationToken cancellationToken)
    {
        if (!gateHeld)
        {
            await runtime.LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await StartWatchingWithGateHeldAsync(runtime, restart: gateHeld, recovery, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!gateHeld)
            {
                runtime.LifecycleGate.Release();
            }
        }
    }

    /// <summary>
    ///     A test seam: awaited with the drive letter by a rescan's or a recovery's restart just
    ///     before it registers the drive's new instance.
    /// </summary>
    internal Func<char, Task>? RestartBeforeRegistrationForTest { get; set; }

    /// <summary>
    ///     A consumer's start (no <paramref name="recovery" />) clears the drive's recovery ticket,
    ///     which it supersedes; a recovery's restart keeps its own.
    /// </summary>
    async Task StartWatchingWithGateHeldAsync(DriveRuntime runtime, bool restart, RecoveryTicket? recovery,
        CancellationToken cancellationToken)
    {
        ThrowIfCancelledByDisposal(cancellationToken);
        var driveLetter = runtime.DriveLetter;
        var source = _options.MftSource?.WatchSource ?? throw new InvalidOperationException(
            _options.MftSource?.UnavailableReason is { } reason
                ? MftIndexSource.FormatUnavailable(driveLetter, reason)
                : $"Drive {driveLetter} supports a live watch but " +
                  $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.MftSource)} has no watch source.");

        (DriveBlock ArmedBlock, Task? PreviousDrain)? preparation;
        try
        {
            preparation = PrepareStart(runtime, restart, recovery);
        }
        finally
        {
            RaiseWatchStateChanged(runtime);
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

        if (restart && RestartBeforeRegistrationForTest is { } beforeRegistration)
        {
            await beforeRegistration(driveLetter).ConfigureAwait(false);
        }

        var registration = RegisterStartingInstance(runtime, armedBlock, restart, recovery);
        RaiseWatchStateChanged(runtime);
        if (registration is not { } registered)
        {
            return;
        }

        await RunRegisteredStartAsync(runtime, source, registered, restart, recovery, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The first step of a start, under <see cref="_stateLock" />: returns null when there is
    ///     nothing to start (a restart a stop has overtaken, or a watch that is already running),
    ///     and otherwise the block to arm from and the teardown to wait for: a retiring instance's,
    ///     or a faulted current instance's, which stays current (so a stop that lands before the
    ///     registration still takes and rethrows its fault) until the registration supersedes it.
    ///     Throws for a drive with no MFT-backed block or an unresumable one.
    /// </summary>
    (DriveBlock ArmedBlock, Task? PreviousDrain)? PrepareStart(DriveRuntime runtime, bool restart,
        RecoveryTicket? recovery)
    {
        var driveLetter = runtime.DriveLetter;
        lock (_stateLock)
        {
            try
            {
                if (restart && !IsRestartStillRequestedLocked(runtime, recovery))
                {
                    return null;
                }

                if (recovery is null && runtime.Recovery is not null)
                {
                    ClearRecoveryLocked(runtime);
                }

                if (FindWatchableDriveBlockLocked(driveLetter) is not { } armedBlock)
                {
                    throw RefuseStartWithoutWatchableBlockLocked(runtime, restart);
                }

                if (_unresumableCheckpointsByOrdinal.TryGetValue(armedBlock.DriveOrdinal, out var unresumable))
                {
                    throw RecordUnresumableCheckpointWatchFailureLocked(runtime, armedBlock, unresumable);
                }

                if (runtime.Current is { State: WatchInstanceState.Running })
                {
                    return null;
                }

                var previousDrain = runtime.Current is { State: WatchInstanceState.Faulted } faulted
                    ? faulted.Drained
                    : runtime.Retiring?.Drained;
                return (armedBlock, previousDrain);
            }
            finally
            {
                // Clearing a recovery and every refusal above change the drive's state.
                NoteWatchStateLocked(runtime);
            }
        }
    }

    readonly record struct RegisteredWatchStart(WatchInstance Instance, IndexWatchTarget Target);

    /// <summary>
    ///     Invokes the source for a registered instance and publishes the handle it returns, or
    ///     settles the start when the source throws or a stop or disposal retired the instance
    ///     while the source ran.
    /// </summary>
    async Task RunRegisteredStartAsync(DriveRuntime runtime, IIndexWatchSource source, RegisteredWatchStart registration,
        bool restart, RecoveryTicket? recovery, CancellationToken cancellationToken)
    {
        var instance = registration.Instance;
        var driveLetter = runtime.DriveLetter;
        IIndexDriveWatch handle;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                instance.StartCancellation.Token, cancellationToken);
            handle = await source.StartAsync(registration.Target, linked.Token).ConfigureAwait(false) ??
                     throw new InvalidOperationException(
                         $"The watch source returned no handle for drive {driveLetter}.");
        }
        catch (Exception exception)
        {
            if (restart && recovery is null && RecordRescanRestartFailure(runtime, instance, exception))
            {
                return;
            }

            AbandonFailedStart(runtime, instance, exception, recovery, cancellationToken);
            throw;
        }

        var published = TryPublishHandle(runtime, instance, handle);
        RaiseWatchStateChanged(runtime);
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
            CompleteInstanceDrain(runtime, instance);
        }

        throw new OperationCanceledException(
            $"The watch for drive {instance.DriveLetter} (start {instance.Generation}) was stopped while it was starting.");
    }

    /// <summary>
    ///     Registers the drive's new instance in <see cref="WatchInstanceState.Starting" /> before
    ///     the source is invoked, so a stop or disposal that arrives while the source runs can
    ///     find the instance and cancel its start. An instance a recovery starts, while that
    ///     recovery's ticket is still the drive's, awaits its first catch-up as the end of that
    ///     recovery (<see cref="RecoveryState.RecoveredAwaitingCatchUp" />). A consumer's start
    ///     sets the watch request; a rescan's or recovery's restart (<paramref name="restart" />)
    ///     only follows it: when a stop has cleared the request, or has cleared the recovery's
    ///     ticket, since the restart read it, this registers nothing and returns null, so a stop
    ///     that returned in that interval wins.
    /// </summary>
    RegisteredWatchStart? RegisterStartingInstance(DriveRuntime runtime,
        DriveBlock armedBlock, bool restart, RecoveryTicket? recovery)
    {
        lock (_stateLock)
        {
            ThrowIfCancelledByDisposal(CancellationToken.None);
            if (restart && !IsRestartStillRequestedLocked(runtime, recovery))
            {
                return null;
            }

            // Its pump has ended and its teardown was awaited, so a faulted instance goes
            // straight to drained. A manual rescan keeps its fault until the start settles.
            if (runtime.Current is { State: WatchInstanceState.Faulted } faulted)
            {
                if (restart && recovery is null)
                {
                    runtime.RescanHandoffFault ??= faulted.OutstandingFault;
                    faulted.OutstandingFault = null;
                }

                _ = RetireCurrentLocked(runtime);
            }

            var instance = new WatchInstance(runtime.DriveLetter, runtime.NextGeneration++, armedBlock,
                TakeRestartPendingWaiterLocked(runtime), DisposalToken);
            runtime.Current = instance;
            if (!restart)
            {
                runtime.WatchRequested = true;
            }

            runtime.RefusedStartFault = null;
            runtime.RecoveryState = recovery is not null && ReferenceEquals(runtime.Recovery, recovery)
                ? RecoveryState.RecoveredAwaitingCatchUp
                : RecoveryState.None;
            _watchFailureMessagesByOrdinal.Remove(armedBlock.DriveOrdinal);
            NoteWatchStateLocked(runtime);
            return new RegisteredWatchStart(instance, BuildWatchTarget(armedBlock));
        }
    }

    /// <summary>
    ///     A restart still stands while the watch is requested and, for a recovery's restart, the
    ///     recovery's ticket is still the drive's. The caller holds <see cref="_stateLock" />.
    /// </summary>
    static bool IsRestartStillRequestedLocked(DriveRuntime runtime, RecoveryTicket? recovery) =>
        runtime.WatchRequested && (recovery is null || ReferenceEquals(runtime.Recovery, recovery));

    /// <summary>
    ///     Settles a start whose source threw. A start that is no longer current was stopped or
    ///     disposed, which already retired it; a start its own caller cancelled leaves the drive
    ///     not watching; any other failure is recorded against the drive. A failed restart of an
    ///     automatic <paramref name="recovery" /> is the failure that
    ///     recovery then reports as <see cref="WatchFaultKind.Recovery" />, so the drive's move to
    ///     <see cref="WatchCatchUpState.Faulted" /> carries that fault.
    /// </summary>
    void AbandonFailedStart(DriveRuntime runtime, WatchInstance instance, Exception exception,
        RecoveryTicket? recovery, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(runtime.Current, instance))
            {
                runtime.Current = null;
                WatchFault? cause = null;
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    runtime.WatchRequested = false;
                    instance.CatchUp.Cancel();
                }
                else
                {
                    runtime.RefusedStartFault = exception;
                    instance.CatchUp.Fault(exception);
                    _watchFailureMessagesByOrdinal[instance.ArmedBlock.DriveOrdinal] = exception.Message;
                    // A stop, a consumer start or disposal that cleared the recovery's ticket also
                    // retired this instance, so a recovery restart still current is still ticketed.
                    if (recovery is not null)
                    {
                        cause = new WatchFault(WatchFaultKind.Recovery, runtime.DriveLetter, exception);
                    }
                }

                NoteWatchStateLocked(runtime, cause);
            }
        }

        RaiseWatchStateChanged(runtime);
        CompleteInstanceDrain(runtime, instance);
    }

    /// <summary>
    ///     The start's linearization point: publishes the handle and starts the pump only while
    ///     the instance is still current and starting.
    /// </summary>
    bool TryPublishHandle(DriveRuntime runtime, WatchInstance instance, IIndexDriveWatch handle)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(runtime.Current, instance) || instance.State != WatchInstanceState.Starting)
            {
                return false;
            }

            instance.State = WatchInstanceState.Running;
            NoteWatchStateLocked(runtime);
            runtime.RescanHandoffFault = null;
            if (runtime.Retiring is not null)
            {
                runtime.Retiring.OutstandingFault = null;
            }

            // Queued rather than run inline, so no source code runs while this lock is held. The
            // pump's own exit path completes the instance's drain, which is what every waiter
            // observes, so the task itself is not kept.
            _ = Task.Run(() => PumpAsync(runtime, instance, handle));
            return true;
        }
    }
}
