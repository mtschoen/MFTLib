using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Clears every drive's watch request, retires every current instance, and waits for every
    ///     retiring instance's teardown, each pump disposing its own handle. Never throws a watch
    ///     fault: each was announced through <see cref="WatchFaulted" /> when it happened, and
    ///     <see cref="WatchInstance.Drained" /> never faults. Runs after the disposal token is
    ///     cancelled, so a start still in progress has already been told to stop.
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
                if (runtime.Retiring is { } alreadyRetiring)
                {
                    drains.Add(alreadyRetiring.Drained);
                }

                if (RetireCurrentLocked(runtime) is { } instance)
                {
                    retired.Add(instance);
                    drains.Add(instance.Drained);
                }
            }
        }

        foreach (var instance in retired)
        {
            instance.RequestStop();
        }

        return Task.WhenAll(drains);
    }

    async Task ReleaseSnapshotsForDisposalAsync(ExceptionDispatchInfo? cancellationFailure)
    {
        await _rescanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _swapGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ReleaseAllRetiredSnapshotsAsync().ConfigureAwait(false);

                Snapshot? current;
                lock (_stateLock)
                {
                    current = _snapshot;
                    _snapshot = null;
                    _driveBlocks.Clear();
                    _retiredSnapshots.Clear();
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
                // The gate is released but deliberately not disposed. A waiter admitted a moment
                // after the flag was set throws ObjectDisposedException from its own re-check, which
                // is a caller's error to handle; disposing the gate would instead throw that from the
                // waiter's finally as it released, hiding the first exception. This SemaphoreSlim
                // never had its AvailableWaitHandle taken, so it holds nothing that needs a
                // deterministic release.
                _swapGate.Release();
            }
        }
        finally
        {
            _rescanGate.Release();
        }
    }
}
