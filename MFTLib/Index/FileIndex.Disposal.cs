using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Prevents new index operations and cancels every rescan in flight, stops every drive's
    ///     live watch and waits for each drive's teardown, takes every drive's lifecycle gate and
    ///     then every drive's write gate in ascending drive-letter order, so no rescan, start,
    ///     commit, or batch is still running, and releases every snapshot it holds, current and
    ///     retired. A fault a drive's watch ended with is never thrown from here: it was announced
    ///     through <see cref="WatchFaulted" /> when it happened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Disposing while another thread is running a scan is safe, and is what this method
    ///         being asynchronous buys. Six entry points scan rows, and each holds a borrow on
    ///         the snapshot it reads for its whole duration: <see cref="Find" />,
    ///         <see cref="Search" />, <see cref="Enumerate" />, <see cref="EnumerateRows" />
    ///         and <see cref="Root" /> on this class, and
    ///         <see cref="FileEntry.Children" /> on a handle. Disposal waits for every one of
    ///         those borrows, on the current snapshot and on the retired ones, before it unmaps
    ///         anything.
    ///     </para>
    ///     <para>
    ///         The five queries on this class also observe a token linked to the index's disposal,
    ///         so disposal cancels them rather than waiting them out: each ends with
    ///         <see cref="OperationCanceledException" /> or, if it had not started,
    ///         <see cref="ObjectDisposedException" />, and the wait is as long as they take to
    ///         reach their next checkpoint, at most 4096 rows of active scanning. A suspended
    ///         <see cref="Enumerate" /> enumerator holds its borrow between yields: disposal waits
    ///         until it advances or is disposed, or, if abandoned, until garbage collection and
    ///         finalization return its borrow. Dispose enumerators promptly rather than relying on
    ///         collection, whose timing is not guaranteed. <see cref="FileEntry.Children" />
    ///         is the exception: a handle carries no reference to its index, so there is no
    ///         disposal token to link and this method waits that listing out instead, which is one
    ///         pass over the drive's rows unless its caller passes a token of its own.
    ///     </para>
    ///     <para>
    ///         Every other member of <see cref="FileEntry" /> reads in place rather than
    ///         scanning, and carries no borrow; <see cref="FileEntry.Path" />, and <see cref="FileEntry.ToString" />
    ///         which can build it, follow the parent chain for at most 128 hops. A read through a handle that is ordered after the
    ///         disposal throws <see cref="ObjectDisposedException" /> from the per-access check
    ///         <see cref="FileEntry.IsDisposed" /> describes.
    ///     </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (RejectInsideHandler(nameof(DisposeAsync)) is { } rejection)
        {
            throw rejection;
        }

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Hooks.DisposedFlagSet?.Invoke();

        // Before the gates, not after: a query already inside the index holds a borrow that every
        // release below waits for, so it has to be told to stop before anything starts waiting on
        // it. A query that has not started yet is turned away by the disposed flag instead. The
        // same token cancels every drive's watch start and every rescan still in progress.
        ExceptionDispatchInfo? cancellationFailure = null;
        try
        {
            await _disposalCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            // A callback on this token belongs to someone else, and a throw from one is not a
            // reason to abandon the mappings: the disposed flag is already set, so the early
            // return above means no later call would ever finish the job. The release runs, and
            // the failure is raised after it, with its original stack, or alongside the release's
            // own failure if the release fails too.
            cancellationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        // No token of its own, and none may abandon a pump whose blocks this is about to unmap.
        await StopEveryWatchForDisposalAsync().ConfigureAwait(false);

        try
        {
            await ReleaseSnapshotsForDisposalAsync(cancellationFailure).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                foreach (var runtime in _driveRuntimes.Values)
                {
                    runtime.ReleaseCanonicalOwnershipLocked();
                }
            }
        }

        cancellationFailure?.Throw();
    }

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
        List<WatchHandoff> handoffs;
        lock (_stateLock)
        {
            handoffs = [.. _driveRuntimes.Values.Select(runtime => runtime.PrepareDisposalLocked())];
        }

        foreach (var runtime in _driveRuntimes.Values)
        {
            runtime.RaiseWatchStateChanged();
        }

        return Task.WhenAll(handoffs.Select(handoff => handoff.DrainAsync()));
    }

    /// <summary>
    ///     The checkpoint of an operation that was admitted before disposal began (a rescan, a
    ///     recovery's scan, a start or a restart): once disposal has begun, the operation is
    ///     cancelled by it and ends with <see cref="OperationCanceledException" />, the outcome
    ///     <see cref="RescanAsync" /> and
    ///     <see cref="StartWatchingAsync" /> document for a disposal during
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
        var runtimes = _driveRuntimes.Values.OrderBy(runtime => runtime.Drive).ToArray();
        foreach (var runtime in runtimes)
        {
            await runtime.LifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Hooks.LifecycleGatesTakenForDisposal?.Invoke();
        foreach (var runtime in runtimes)
        {
            await runtime.WriteGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            foreach (var runtime in runtimes)
            {
                await runtime.ReleaseRetiredBlocksAsync().ConfigureAwait(false);
            }

            DriveBlock[]? current;
            lock (_stateLock)
            {
                current = _snapshot;
                _snapshot = null;
                foreach (var runtime in runtimes)
                {
                    runtime.DetachCurrentBlockLocked();
                }
            }

            // Unconditional: a consumer that disposed everything it owns has asked for the
            // mappings to go, and a handle it kept is answered by ObjectDisposedException
            // rather than by an indefinitely open block file. See FileEntry.IsDisposed.
            // Released outside _stateLock because this waits out both a release another caller
            // already started and every query still reading the snapshot, and a reader must not
            // be shut out of the lock for that long.
            foreach (var driveBlock in current ?? [])
            {
                await driveBlock.ReleaseState.DrainAndReleaseAsync().ConfigureAwait(false);
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

    /// <summary>
    ///     Unwinds every block already added when opening fails or
    ///     is cancelled partway through <see cref="FileIndexOptions.Drives" />. These blocks were
    ///     never published, so no handle holds one, and none has a scheduled delete. A warm-started block's
    ///     file is left on disk (still valid for the next open); a freshly cold-scanned block's
    ///     file is left on disk too when cached, or deleted immediately when it was created with
    ///     delete-on-close for no-cache mode.
    /// </summary>
    void ReleaseUnpublishedBlocks()
    {
        lock (_stateLock)
        {
            foreach (var runtime in _driveRuntimes.Values)
            {
                runtime.ReleaseUnpublishedBlockLocked();
            }
        }
    }
}
