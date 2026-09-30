using System.Runtime.ExceptionServices;

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
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="driveLetter" /> is not part of this index.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The drive has no MFT-backed block, <see cref="FileIndexOptions.WatchSource" /> is not set,
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
    ///     Stops one drive's watch and waits for its teardown, then rethrows, once, the fault that
    ///     ended the watch or the first subscriber fault it announced, if either is outstanding.
    ///     Takes no lifecycle gate, so it never waits for a rescan: a stop during a rescan of the
    ///     same drive completes while the scan runs, and the rescan then leaves the watch stopped.
    ///     The same holds for an automatic recovery, and a queued one ends without scanning.
    ///     A manual rescan retains the retired watch's fault through its drain and replacement
    ///     start, so a stop before the replacement handle is published still takes that fault.
    ///     The drive's <see cref="DriveStatus.WatchCatchUp" /> reads
    ///     <see cref="WatchCatchUpState.NotStarted" /> afterwards and any pending catch-up wait is
    ///     cancelled. A drive counts as watching while its watch is requested or it has a watch
    ///     instance, current or still retiring; a start that failed at its source leaves the watch
    ///     requested, so stopping that drive clears the request and its faulted state.
    ///     <paramref name="cancellationToken" /> bounds only the wait for the teardown: cancelling
    ///     it throws while the teardown continues, and a later start waits for that teardown.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="driveLetter" /> is not part of this index.</exception>
    /// <exception cref="InvalidOperationException">The drive is not watching.</exception>
    public async Task StopWatchingAsync(char driveLetter, CancellationToken cancellationToken)
    {
        if (RejectInsideHandler(nameof(StopWatchingAsync)) is { } rejection)
        {
            throw rejection;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = GetDriveRuntime(driveLetter);
        WatchInstance? retired;
        Task? previousDrain;
        Exception? outstandingFault;
        lock (_stateLock)
        {
            if (!IsWatchingLocked(runtime))
            {
                throw new InvalidOperationException(
                    $"Drive {runtime.DriveLetter} is not watching, so there is no watch to stop.");
            }

            runtime.WatchRequested = false;
            runtime.RefusedStartFault = null;
            ClearRecoveryLocked(runtime);
            retired = RetireCurrentLocked(runtime);
            outstandingFault = retired?.OutstandingFault ?? runtime.RescanHandoffFault;
            runtime.RescanHandoffFault = null;
            if (retired is not null)
            {
                retired.OutstandingFault = null;
            }

            previousDrain = runtime.Retiring?.Drained;
        }

        retired?.RequestStop();
        var drain = retired?.Drained ?? previousDrain;
        if (drain is not null)
        {
            await AwaitQueuedAsync(drain, cancellationToken, CancellationToken.None).ConfigureAwait(false);
        }

        if (outstandingFault is not null)
        {
            ExceptionDispatchInfo.Capture(outstandingFault).Throw();
        }
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
        var source = _options.WatchSource ?? throw new InvalidOperationException(
            $"Drive {driveLetter} supports a live watch but " +
            $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.WatchSource)} is not set.");

        if (PrepareStart(runtime, restart, recovery) is not { } prepared)
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

        if (RegisterStartingInstance(runtime, armedBlock, restart, recovery) is not { } registered)
        {
            return;
        }

        var (instance, target) = registered;
        await RunRegisteredStartAsync(runtime, source, instance, target, rescanRestart: restart && recovery is null,
            cancellationToken).ConfigureAwait(false);
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
            if (restart && !IsRestartStillRequestedLocked(runtime, recovery))
            {
                return null;
            }

            if (recovery is null && runtime.Recovery is not null)
            {
                ClearRecoveryLocked(runtime);
            }

            var armedBlock = FindWatchableDriveBlockLocked(driveLetter) ?? throw new InvalidOperationException(
                $"Drive {driveLetter} has no MFT-backed block, so there is no journal cursor to watch from.");
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
    }

    /// <summary>
    ///     Invokes the source for a registered instance and publishes the handle it returns, or
    ///     settles the start when the source throws or a stop or disposal retired the instance
    ///     while the source ran.
    /// </summary>
    async Task RunRegisteredStartAsync(DriveRuntime runtime, IIndexWatchSource source, WatchInstance instance,
        IndexWatchTarget target, bool rescanRestart, CancellationToken cancellationToken)
    {
        var driveLetter = runtime.DriveLetter;
        IIndexDriveWatch handle;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                instance.StartCancellation.Token, cancellationToken);
            handle = await source.StartAsync(target, linked.Token).ConfigureAwait(false) ??
                     throw new InvalidOperationException(
                         $"The watch source returned no handle for drive {driveLetter}.");
        }
        catch (Exception exception)
        {
            if (rescanRestart && RecordRescanRestartFailure(runtime, instance, exception))
            {
                return;
            }

            AbandonFailedStart(runtime, instance, exception, cancellationToken);
            throw;
        }

        if (TryPublishHandle(runtime, instance, handle))
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
    (WatchInstance Instance, IndexWatchTarget Target)? RegisterStartingInstance(DriveRuntime runtime,
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
                }

                _ = RetireCurrentLocked(runtime);
            }

            var instance = new WatchInstance(runtime.DriveLetter, runtime.NextGeneration++, armedBlock, DisposalToken);
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
            return (instance, BuildWatchTarget(armedBlock));
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
    ///     not watching; any other failure is recorded against the drive.
    /// </summary>
    void AbandonFailedStart(DriveRuntime runtime, WatchInstance instance, Exception exception,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(runtime.Current, instance))
            {
                runtime.Current = null;
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
                }
            }
        }

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
            runtime.RescanHandoffFault = null;

            // Queued rather than run inline, so no source code runs while this lock is held. The
            // pump's own exit path completes the instance's drain, which is what every waiter
            // observes, so the task itself is not kept.
            _ = Task.Run(() => PumpAsync(runtime, instance, handle));
            return true;
        }
    }
}
