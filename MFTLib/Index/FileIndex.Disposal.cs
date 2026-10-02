using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Clears every drive's watch request and recovery ticket, retires every current instance,
    ///     and waits for every retiring instance's teardown, each pump disposing its own handle,
    ///     and for every recovery still running. Never throws a watch fault: each was announced
    ///     through <see cref="WatchFaulted" /> when it happened, and neither
    ///     <see cref="WatchInstance.Drained" /> nor a recovery's completion ever faults. Runs after
    ///     the disposal token is cancelled, so a start or recovery still in progress has already
    ///     been told to stop.
    /// </summary>
    Task StopEveryWatchForDisposalAsync()
    {
        var retired = new List<WatchInstance>();
        var drains = new List<Task>();
        lock (_stateLock)
        {
            foreach (var runtime in _driveRuntimes.Values)
            {
                runtime.WatchRequested = false;
                runtime.RefusedStartFault = null;
                CancelRestartPendingWaiterLocked(runtime);
                ClearRecoveryLocked(runtime);
                if (runtime.Retiring is { } alreadyRetiring)
                {
                    drains.Add(alreadyRetiring.Drained);
                }

                if (RetireCurrentLocked(runtime) is { } instance)
                {
                    retired.Add(instance);
                    drains.Add(instance.Drained);
                }

                NoteWatchStateLocked(runtime);
            }

            drains.AddRange(_recoveryCompletions);
        }

        foreach (var runtime in _driveRuntimes.Values)
        {
            RaiseWatchStateChanged(runtime);
        }

        foreach (var instance in retired)
        {
            instance.RequestStop();
        }

        return Task.WhenAll(drains);
    }

    /// <summary>
    ///     A test seam: invoked once disposal holds every drive's lifecycle gate, before it waits for
    ///     any write gate.
    /// </summary>
    internal Action? LifecycleGatesTakenForDisposalForTest { get; set; }

    /// <summary>
    ///     A test seam: invoked once disposal has set the disposed flag, before it cancels the
    ///     disposal token.
    /// </summary>
    internal Action? DisposedFlagSetForTest { get; set; }

    /// <summary>
    ///     The checkpoint of an operation that was admitted before disposal began (a rescan, a
    ///     recovery's scan, a start or a restart): once disposal has begun, the operation is
    ///     cancelled by it and ends with <see cref="OperationCanceledException" />, the outcome
    ///     <see cref="RescanAsync(char, CancellationToken)" /> and
    ///     <see cref="StartWatchingAsync(char, CancellationToken)" /> document for a disposal during
    ///     the call. The operation's own token is not enough to tell: disposal sets the flag before
    ///     it cancels the disposal token, and a token linked to the disposal token is cancelled by a
    ///     callback that <see cref="CancellationTokenSource.CancelAsync" /> runs later still.
    ///     <see cref="ObjectDisposedException" /> stays the answer for a call made after disposal
    ///     began, which each public entry point checks before it is admitted.
    /// </summary>
    void ThrowIfCancelledByDisposal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            throw new OperationCanceledException("The index is being disposed.", DisposalToken);
        }
    }

    /// <summary>
    ///     Takes every drive's lifecycle gate, then every drive's write gate, each in ascending
    ///     drive-letter order, so no rescan, start, commit, or batch is still running, then releases
    ///     every snapshot, retired and current, and finally gives the gates back. The gates are
    ///     released but never disposed: an operation admitted before the disposed flag was set may
    ///     still be waiting for a gate, and once it takes the gate its checkpoint ends it with
    ///     <see cref="OperationCanceledException" /> (see <see cref="ThrowIfCancelledByDisposal" />),
    ///     while a disposed gate would throw from that operation's wait or release instead and hide
    ///     the cancellation. A call made after the flag was set never reaches a gate: its public
    ///     entry point throws <see cref="ObjectDisposedException" />. None of these gates ever
    ///     had its <see cref="SemaphoreSlim.AvailableWaitHandle" /> taken, so none holds anything
    ///     that needs a deterministic release.
    /// </summary>
    async Task ReleaseSnapshotsForDisposalAsync(ExceptionDispatchInfo? cancellationFailure)
    {
        var runtimes = _driveRuntimes.Values.OrderBy(runtime => runtime.DriveLetter).ToArray();
        foreach (var runtime in runtimes)
        {
            await runtime.LifecycleGate.WaitAsync().ConfigureAwait(false);
        }

        LifecycleGatesTakenForDisposalForTest?.Invoke();
        foreach (var runtime in runtimes)
        {
            await runtime.WriteGate.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            SnapshotRelease[] retiredSnapshots;
            lock (_stateLock)
            {
                retiredSnapshots = [.. _retiredSnapshots];
                _retiredSnapshots.Clear();
            }

            foreach (var retired in retiredSnapshots)
            {
                await retired.ReleaseAsync().ConfigureAwait(false);
            }

            Snapshot? current;
            lock (_stateLock)
            {
                current = _snapshot;
                _snapshot = null;
                _driveBlocks.Clear();
            }

            // Unconditional: a consumer that disposed everything it owns has asked for the
            // mappings to go, and a handle it kept is answered by ObjectDisposedException
            // rather than by an indefinitely open block file. See FileEntry.IsDisposed.
            // Released outside _stateLock because this waits out both a release another caller
            // already started and every query still reading the snapshot, and a reader must not
            // be shut out of the lock for that long.
            if (current is not null)
            {
                await current.ReleaseNowAsync().ConfigureAwait(false);
            }
        }
        catch (Exception releaseFailure) when (cancellationFailure is not null)
        {
            // Both halves failed. The captured cancellation failure has nowhere left to go once
            // this one is in flight, and a consumer told the release failed would never learn
            // that its own callback threw first, so both come out together, in the order they
            // happened. The first is itself the AggregateException CancelAsync raised, so a
            // consumer that wants the leaves calls Flatten().
            throw new AggregateException(cancellationFailure.SourceException, releaseFailure);
        }
        finally
        {
            foreach (var runtime in runtimes)
            {
                runtime.WriteGate.Release();
                runtime.LifecycleGate.Release();
            }
        }
    }
}
