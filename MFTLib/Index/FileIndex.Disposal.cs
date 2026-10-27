using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Prevents new index operations and cancels every rescan in flight, stops the root's
    ///     live watch and waits for its teardown, takes the lifecycle gate and
    ///     then the write gate, so no rescan, start, commit, or batch is still running, and
    ///     releases every block it holds, current and retired. A fault the watch ended with is
    ///     never thrown from here: it was announced through <see cref="WatchFaulted" /> when it happened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Disposing while another thread is running a scan is safe, and is what this method
    ///         being asynchronous buys. Six entry points scan rows, and each holds a borrow on
    ///         the block it reads for its whole duration: <see cref="Find" />,
    ///         <see cref="Search" />, <see cref="Enumerate" />, <see cref="EnumerateRows" />
    ///         and <see cref="Root" /> on this class, and
    ///         <see cref="FileEntry.Children" /> on a handle. Disposal waits for every one of
    ///         those borrows, on the current block and on the retired ones, before it unmaps
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
    ///         pass over the root's rows unless its caller passes a token of its own.
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
        // same token cancels the root's watch start and any rescan still in progress.
        ExceptionDispatchInfo? cancellationFailure = null;
        try
        {
            await _disposalCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            cancellationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        // No token of its own, and none may abandon a pump whose block this is about to unmap.
        await StopWatchForDisposalAsync().ConfigureAwait(false);

        try
        {
            await ReleaseBlocksForDisposalAsync(cancellationFailure).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                _runtime.ReleaseCanonicalOwnershipLocked();
            }
        }

        cancellationFailure?.Throw();
    }

    /// <summary>
    ///     Clears the root's watch request and recovery ticket, retires the current instance,
    ///     and waits for every retiring instance's teardown and for every recovery still running.
    /// </summary>
    Task StopWatchForDisposalAsync()
    {
        WatchHandoff handoff;
        lock (_stateLock)
        {
            handoff = _runtime.PrepareDisposalLocked();
        }

        _runtime.RaiseWatchStateChanged();
        return handoff.DrainAsync();
    }

    /// <summary>
    ///     The checkpoint of an operation that was admitted before disposal began (a rescan, a
    ///     recovery's scan, a start or a restart): once disposal has begun, the operation is
    ///     cancelled by it and ends with <see cref="OperationCanceledException" />.
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
    ///     Takes the root's lifecycle gate, then its write gate, so no rescan, start, commit, or
    ///     batch is still running, then releases every block, retired and current, and finally
    ///     gives the gates back.
    /// </summary>
    async Task ReleaseBlocksForDisposalAsync(ExceptionDispatchInfo? cancellationFailure)
    {
        await _runtime.LifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Hooks.LifecycleGatesTakenForDisposal?.Invoke();
        await _runtime.WriteGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await _runtime.ReleaseRetiredBlocksAsync().ConfigureAwait(false);

            RootBlock? current;
            lock (_stateLock)
            {
                current = _runtime.DetachCurrentBlockLocked();
            }

            if (current is not null)
            {
                await current.ReleaseState.DrainAndReleaseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception releaseFailure) when (cancellationFailure is not null)
        {
            throw new AggregateException(cancellationFailure.SourceException, releaseFailure);
        }
        finally
        {
            _runtime.WriteGate.Release();
            _runtime.LifecycleGate.Release();
        }
    }

    /// <summary>
    ///     Unwinds any block already adopted when opening fails or is cancelled.
    /// </summary>
    void ReleaseUnpublishedBlocks()
    {
        lock (_stateLock)
        {
            _runtime.ReleaseUnpublishedBlockLocked();
        }
    }
}
