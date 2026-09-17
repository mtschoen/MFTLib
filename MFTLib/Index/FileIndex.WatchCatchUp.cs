using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One watched drive's catch-up state, keyed by drive ordinal like
    ///     <see cref="_watchFailureMessagesByOrdinal" />. An entry exists from the drive's arm
    ///     until the session that armed it is reclaimed; a missing entry reads as
    ///     <see cref="WatchCatchUpState.NotStarted" />.
    /// </summary>
    readonly Dictionary<ushort, WatchCatchUpSlot> _watchCatchUpByOrdinal = [];

    /// <summary>
    ///     Starts one drive's catch-up tracking: a fresh slot reading
    ///     <see cref="WatchCatchUpState.CatchingUp" />, replacing whatever the drive's previous arm
    ///     left. The caller holds <see cref="_stateLock" />.
    /// </summary>
    void ArmWatchCatchUpLocked(char driveLetter)
    {
        lock (_stateLock)
        {
            if (TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal))
            {
                if (_watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var previous))
                {
                    previous.Cancel();
                }

                _watchCatchUpByOrdinal[driveOrdinal] =
                    new WatchCatchUpSlot { State = WatchCatchUpState.CatchingUp };
            }
        }
    }

    /// <summary>The lock-taking counterpart the rescan path calls outside the pump.</summary>
    void ArmWatchCatchUp(char driveLetter)
    {
        lock (_stateLock)
        {
            ArmWatchCatchUpLocked(driveLetter);
        }
    }

    /// <summary>
    ///     Flips one drive to <see cref="WatchCatchUpState.CaughtUp" /> when its arm's marker
    ///     arrives. A marker for a drive that is not catching up (unknown, superseded, already
    ///     settled) is stale and ignored.
    /// </summary>
    void CompleteWatchCatchUp(char driveLetter)
    {
        lock (_stateLock)
        {
            if (TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal) &&
                _watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var slot) &&
                slot.State == WatchCatchUpState.CatchingUp)
            {
                slot.State = WatchCatchUpState.CaughtUp;
                slot.Waiter.TrySetResult();
            }
        }
    }

    /// <summary>
    ///     Flips one drive to <see cref="WatchCatchUpState.Faulted" /> alongside its watch failure
    ///     message. The caller holds <see cref="_stateLock" />.
    /// </summary>
    void FaultWatchCatchUpLocked(ushort driveOrdinal, Exception exception)
    {
        lock (_stateLock)
        {
            if (!_watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var slot))
            {
                slot = new WatchCatchUpSlot();
                _watchCatchUpByOrdinal[driveOrdinal] = slot;
            }

            slot.Fault(exception);
        }
    }

    /// <summary>
    ///     Ends every drive's catch-up tracking when the session that armed them is reclaimed:
    ///     with nothing draining, no catch-up state is true of any drive. The caller holds
    ///     <see cref="_stateLock" />.
    /// </summary>
    void ResetWatchCatchUpLocked()
    {
        lock (_stateLock)
        {
            foreach (var slot in _watchCatchUpByOrdinal.Values)
            {
                slot.Cancel();
            }

            _watchCatchUpByOrdinal.Clear();
        }
    }

    /// <summary>
    ///     Cancels any drive catch-up wait that is still catching up when the session is cancelled.
    ///     The caller holds <see cref="_stateLock" />.
    /// </summary>
    void CancelPendingWatchCatchUpLocked()
    {
        lock (_stateLock)
        {
            foreach (var slot in _watchCatchUpByOrdinal.Values)
            {
                if (slot.State == WatchCatchUpState.CatchingUp)
                {
                    slot.Cancel();
                }
            }
        }
    }

    /// <summary>
    ///     Faults every watched drive's catch-up wait when the stream encounters an unhandled source
    ///     exception. The caller holds <see cref="_stateLock" />.
    /// </summary>
    void FaultPendingWatchCatchUpLocked(IReadOnlyList<IndexWatchTarget> targets, Exception exception)
    {
        lock (_stateLock)
        {
            foreach (var target in targets)
            {
                if (TryGetDriveOrdinalLocked(target.DriveLetter, out var driveOrdinal))
                {
                    FaultWatchCatchUpLocked(driveOrdinal, exception);
                }
            }
        }
    }

    WatchCatchUpState GetWatchCatchUpState(ushort driveOrdinal)
    {
        lock (_stateLock)
        {
            return _watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var slot)
                ? slot.State
                : WatchCatchUpState.NotStarted;
        }
    }

    /// <summary>
    ///     Removes a stale <see cref="WatchCatchUpState.Faulted" /> slot when a rescan clears the
    ///     drive's failure message with no session to arm the drive back onto: the fault is gone
    ///     with its message, and nothing is catching up either.
    /// </summary>
    void RemoveStaleFaultedCatchUp(char driveLetter)
    {
        lock (_stateLock)
        {
            if (TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal) &&
                _watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var slot) &&
                slot.State == WatchCatchUpState.Faulted)
            {
                _watchCatchUpByOrdinal.Remove(driveOrdinal);
            }
        }
    }

    /// <summary>
    ///     Waits for one watched drive's initial journal catch-up: the returned task completes
    ///     once the backlog present when the drive's current arm started has been applied and the
    ///     drive is on live entries. It completes immediately when the drive is already caught up,
    ///     faults with the drive's exception when the drive's watch fails (before or after this
    ///     call), and is cancelled when the arm it tracks is superseded by a rescan's re-arm or
    ///     when the watch session ends. The wait is linked to the index's disposal token, so
    ///     disposing the index cancels it. A wait issued while its drive is mid-rescan tracks the
    ///     arm that is current at that moment; issue the wait after
    ///     <see cref="RescanAsync" /> returns to track the re-armed catch-up.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="driveLetter" /> is not part of this index.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The drive is not being watched: no watch session is running, the drive is not among the
    ///     session's targets, or the drive cannot be watched.
    /// </exception>
    public Task WaitForCatchUpAsync(char driveLetter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var upperDriveLetter = char.ToUpperInvariant(driveLetter);
        if (!_driveConfigurations.ContainsKey(upperDriveLetter))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        Task wait;
        lock (_stateLock)
        {
            if (_watchSession is not { } session ||
                !session.Targets.Any(target => char.ToUpperInvariant(target.DriveLetter) == upperDriveLetter) ||
                !TryGetDriveOrdinalLocked(upperDriveLetter, out var driveOrdinal) ||
                !_watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var slot))
            {
                throw new InvalidOperationException(
                    $"Drive {driveLetter} is not being watched, so there is no catch-up to wait for.");
            }

            wait = slot.Task;
        }

        return WaitCatchUpLinkedAsync(wait, cancellationToken);
    }

    /// <summary>
    ///     Waits for every watched drive's initial journal catch-up: the returned task completes
    ///     when the slowest watched drive is caught up and faults with the first drive's watch
    ///     failure. Cancellation, disposal, and arm supersession behave as in
    ///     <see cref="WaitForCatchUpAsync(char, CancellationToken)" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">No watch session is running.</exception>
    public Task WaitForCatchUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        Task wait;
        CatchUpCoordinator? coordinator = null;
        lock (_stateLock)
        {
            if (_watchSession is not { } session)
            {
                throw new InvalidOperationException("No watch session is running, so there is no catch-up to wait for.");
            }

            var slots = new List<WatchCatchUpSlot>(session.Targets.Count);
            foreach (var target in session.Targets)
            {
                if (!TryGetDriveOrdinalLocked(target.DriveLetter, out var driveOrdinal) ||
                    !_watchCatchUpByOrdinal.TryGetValue(driveOrdinal, out var slot))
                {
                    throw new InvalidOperationException(
                        $"Drive {target.DriveLetter} is not being watched, so there is no catch-up to wait for.");
                }

                if (slot.State == WatchCatchUpState.Faulted && slot.Exception is not null)
                {
                    return WaitCatchUpLinkedAsync(Task.FromException(slot.Exception), cancellationToken);
                }

                slots.Add(slot);
            }

            wait = WhenAllCatchUpAsync(slots, out coordinator);
        }

        return WaitCatchUpLinkedAsync(wait, cancellationToken, coordinator);
    }

    static Task WhenAllCatchUpAsync(IReadOnlyList<WatchCatchUpSlot> slots, out CatchUpCoordinator? coordinator)
    {
        coordinator = null;
        if (slots.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (slots.Count == 1)
        {
            return slots[0].Task;
        }

        if (TryGetSettledCatchUpTask(slots, out var settledTask))
        {
            return settledTask;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator = new CatchUpCoordinator(slots, completionSource);
        coordinator.Attach();
        return completionSource.Task;
    }

    static bool TryGetSettledCatchUpTask(
        IReadOnlyList<WatchCatchUpSlot> slots,
        [NotNullWhen(true)] out Task? settledTask)
    {
        foreach (var slot in slots)
        {
            if (slot.State == WatchCatchUpState.Faulted && slot.Exception is not null)
            {
                settledTask = Task.FromException(slot.Exception);
                return true;
            }

            if (slot.FaultWaiter.Task.IsFaulted)
            {
                settledTask = slot.FaultWaiter.Task;
                return true;
            }

            if (slot.Task.IsFaulted)
            {
                settledTask = slot.Task;
                return true;
            }

            if (slot.WasCanceled || slot.FaultWaiter.Task.IsCanceled || slot.Task.IsCanceled)
            {
                settledTask = Task.FromCanceled(new CancellationToken(true));
                return true;
            }
        }

        foreach (var slot in slots)
        {
            if (slot.State != WatchCatchUpState.CaughtUp || !slot.Task.IsCompletedSuccessfully)
            {
                settledTask = null;
                return false;
            }
        }

        settledTask = Task.CompletedTask;
        return true;
    }

    /// <summary>
    ///     Links one wait to the disposal token (and the caller's, when it has one), which is the
    ///     query contract: disposing the index cancels the wait rather than waiting it out.
    /// </summary>
    async Task WaitCatchUpLinkedAsync(Task wait, CancellationToken cancellationToken, CatchUpCoordinator? coordinator = null)
    {
        if (wait.IsCompletedSuccessfully)
        {
            return;
        }

        if (wait.IsFaulted || wait.IsCanceled)
        {
            await wait.ConfigureAwait(false);
            return;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            try
            {
                await wait.WaitAsync(DisposalToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                coordinator?.Cancel();
                throw;
            }

            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposalToken);
        try
        {
            await wait.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            coordinator?.Cancel();
            throw;
        }
    }
}
