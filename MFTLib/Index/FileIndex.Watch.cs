using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Raised once per applied change, in the order the journal batch delivered them, to
    ///     every subscriber, even when an earlier subscriber threw for an earlier change (or for
    ///     this one). The mutation and the USN cursor are already durable by the time any handler
    ///     runs: <see cref="ApplyJournalEntries" /> applies the whole batch and releases its gate
    ///     before raising this event at all, so a throwing handler never undoes anything and never
    ///     stops another handler from seeing the rest of the batch. See
    ///     <see cref="ApplyJournalEntries" /> for how a handler exception is surfaced to the
    ///     caller.
    /// </summary>
    public event Action<FileChange>? Changed;

    /// <summary>
    ///     Raised immediately when the watch first sees a subscriber fault in a session, and every
    ///     time it drops a drive for an apply or source failure. Exceptions thrown by fault
    ///     handlers are discarded.
    /// </summary>
    public event Action<WatchFault>? WatchFaulted;

    /// <summary>
    ///     Starts one pump over every MFT-backed drive. Each drive resumes from the journal cursor
    ///     persisted in its current block header, and every armed drive's
    ///     <see cref="DriveStatus.WatchFailureMessage" /> is cleared. Cancelling
    ///     <paramref name="cancellationToken" /> ends the session and raises no fault; the session
    ///     is reclaimed by <see cref="StopWatchingAsync" /> or <see cref="DisposeAsync" />. An index
    ///     with no watchable drives has nothing to start and completes immediately. A source whose
    ///     stream ends while drives are still watched, without a stop and without cancellation,
    ///     raises a <see cref="WatchFaultKind.Source" /> fault carrying no drive letter, marks
    ///     every watched drive's <see cref="DriveStatus.WatchFailureMessage" />, and releases the
    ///     session, so this method can be called again to start a fresh one.
    /// </summary>
    public Task StartWatchingAsync(CancellationToken cancellationToken)
    {
        return StartWatchingCoreAsync(cancellationToken, cancellationToken);
    }

    /// <summary>
    ///     Not an <c>async</c> method, because nothing here awaits once the pump yields on its
    ///     own, so its validation throws synchronously rather than through the returned task.
    ///     <paramref name="sessionToken" /> is what the session's lifetime is linked to and
    ///     <paramref name="startCancellationToken" /> only guards this call, which is how a rescan
    ///     restarts a whole session without reparenting the watch's lifetime to itself.
    /// </summary>
    Task StartWatchingCoreAsync(CancellationToken sessionToken, CancellationToken startCancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        startCancellationToken.ThrowIfCancellationRequested();

        var targets = BuildWatchTargets();
        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        var source = _options.WatchSource ?? throw new InvalidOperationException(
            $"{targets.Count} drive(s) support a live watch but " +
            $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.WatchSource)} is not set.");

        lock (_stateLock)
        {
            if (_watchSession is not null)
            {
                throw new InvalidOperationException("This index is already watching.");
            }

            ClearWatchFailures(targets);
            var session = new WatchSession(
                CancellationTokenSource.CreateLinkedTokenSource(sessionToken), source, sessionToken);
            session.Pump = PumpAsync(session, targets);
            _watchSession = session;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Cancels the current watch, waits for its pump to finish, and rethrows the first fault
    ///     observed during the session. Calling this when no watch is active has no effect.
    ///     <paramref name="cancellationToken" /> bounds the wait: cancelling it abandons the wait
    ///     and throws, and deliberately leaves the session in place so a later stop or
    ///     <see cref="DisposeAsync" /> can still reclaim it. A source that ignores the token this
    ///     call cancels is the only thing that can make that wait outlast the caller's patience.
    /// </summary>
    public async Task StopWatchingAsync(CancellationToken cancellationToken)
    {
        WatchSession? session;
        lock (_stateLock)
        {
            session = _watchSession;
        }

        if (session is null)
        {
            return;
        }

        Exception? firstFault = null;
        var pumpFinished = false;
        try
        {
            await session.Cancellation.CancelAsync().ConfigureAwait(false);
            firstFault = await session.Pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            pumpFinished = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancellation is how a stop terminates a source waiting for its next batch. The
            // filter separates that from this call's own token being cancelled, which is an
            // abandoned wait over a pump that is still running.
            pumpFinished = true;
        }
        catch (ObjectDisposedException) when (!IsCurrentWatchSession(session))
        {
            // The pump released this session, and disposed its cancellation with it, between the
            // read above and the cancel: the source ended without being stopped. There is nothing
            // left to stop, and that end was announced through WatchFaulted when it was observed.
        }
        finally
        {
            if (pumpFinished)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_watchSession, session))
                    {
                        _watchSession = null;
                    }
                }

                session.Cancellation.Dispose();
            }
        }

        if (firstFault is not null)
        {
            ExceptionDispatchInfo.Capture(firstFault).Throw();
        }
    }

    bool IsCurrentWatchSession(WatchSession session)
    {
        lock (_stateLock)
        {
            return ReferenceEquals(_watchSession, session);
        }
    }

    List<IndexWatchTarget> BuildWatchTargets()
    {
        lock (_stateLock)
        {
            var targets = new List<IndexWatchTarget>(_driveBlocks.Count);
            foreach (var driveBlock in _driveBlocks)
            {
                if (driveBlock.ProducerKind == ProducerKind.Mft)
                {
                    targets.Add(BuildWatchTarget(driveBlock));
                }
            }

            return targets;
        }
    }

    /// <summary>One drive's counterpart to <see cref="BuildWatchTargets" />, for a re-arm.</summary>
    IndexWatchTarget BuildWatchTarget(char driveLetter)
    {
        lock (_stateLock)
        {
            foreach (var driveBlock in _driveBlocks)
            {
                if (char.ToUpperInvariant(driveBlock.DriveLetter) == char.ToUpperInvariant(driveLetter))
                {
                    return BuildWatchTarget(driveBlock);
                }
            }
        }

        throw new InvalidOperationException($"Drive {driveLetter} has no block to resume a watch from.");
    }

    /// <summary>The one place a drive's persisted journal cursor is read.</summary>
    static IndexWatchTarget BuildWatchTarget(DriveBlock driveBlock)
    {
        ref readonly var header = ref driveBlock.Block.Header;
        return new IndexWatchTarget(driveBlock.DriveLetter, header.UsnJournalId, header.UsnNextUsn);
    }

    /// <summary>
    ///     Arming a drive clears its watch failure entry: a message about a failure on a stream
    ///     that is being restarted is no longer true. The caller holds <see cref="_stateLock" />.
    /// </summary>
    void ClearWatchFailures(IReadOnlyList<IndexWatchTarget> targets)
    {
        foreach (var target in targets)
        {
            ClearWatchFailureLocked(target.DriveLetter);
        }
    }

    /// <summary>
    ///     Resolves the ordinal from <see cref="_driveBlocks" /> directly rather than through
    ///     <see cref="TryGetDriveOrdinal" />, which throws once this index is disposed: a rescan
    ///     clearing an entry on an index that is going away has nothing to clear, not a different
    ///     exception to raise over the disposal the caller is already handling. The caller holds
    ///     <see cref="_stateLock" />.
    /// </summary>
    void ClearWatchFailureLocked(char driveLetter)
    {
        if (TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal))
        {
            _watchFailureMessagesByOrdinal.Remove(driveOrdinal);
        }
    }

    /// <summary>
    ///     Applies one journal batch to a drive's block in place and raises
    ///     <see cref="Changed" /> for each applied change. This is the seam the watch pipeline
    ///     drives; it returns the batch so a caller can act on it without subscribing. The watch
    ///     pump calls <see cref="ApplyJournalEntriesCore" /> and raises <see cref="Changed" />
    ///     itself, so it can tell an apply failure from a subscriber failure.
    /// </summary>
    /// <remarks>
    ///     The mutation runs under <see cref="_swapGate" />, the same gate
    ///     <see cref="RescanAsync" /> holds while it swaps a drive's block, so a rescan in flight
    ///     and a journal batch can never write the same block at once: a rescan builds an
    ///     entirely new block file and only touches <see cref="_driveBlocks" /> under the gate,
    ///     and a journal batch takes its snapshot and its <see cref="BlockWriter" /> under the
    ///     same gate, so it always mutates the block that is current once it is its turn, never a
    ///     block a concurrent rescan is about to supersede. The gate is index-wide rather than
    ///     per-drive, so a batch on one drive also blocks a rescan or another batch on a different
    ///     drive; accepted for v1, since every mutation is already a fast in-place row write, not
    ///     an I/O-bound scan. The gate is released before <see cref="Changed" /> is raised, so a
    ///     subscriber's handler never runs while a rescan is blocked waiting on this call.
    /// </remarks>
    public IReadOnlyList<FileChange> ApplyJournalEntries(char driveLetter,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn)
    {
        var changes = ApplyJournalEntriesCore(driveLetter, entries, journalId, nextUsn);
        RaiseChanged(changes);
        return changes;
    }

    /// <summary>
    ///     Everything <see cref="ApplyJournalEntries" /> does up to and including releasing
    ///     <see cref="_swapGate" />, without raising <see cref="Changed" />.
    /// </summary>
    internal IReadOnlyList<FileChange> ApplyJournalEntriesCore(char driveLetter,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entries);
        var upperDriveLetter = char.ToUpperInvariant(driveLetter);
        if (!_driveConfigurations.ContainsKey(upperDriveLetter))
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        if (!TryGetDriveOrdinal(driveLetter, out var driveOrdinal))
        {
            throw new InvalidOperationException(
                $"Drive {driveLetter} is offline and has no block to apply journal entries to.");
        }

        // ApplyJournalEntries is a synchronous seam by design (the brief's public signature
        // returns IReadOnlyList<FileChange> directly, not a Task), so this blocks on the
        // SemaphoreSlim itself, not on a Task: it is the synchronous counterpart to
        // RescanAsync's WaitAsync, not sync-over-async. An automated scanner can mistake any
        // ".Wait()" call for blocking on a Task; this one is not.
        _swapGate.Wait();
        try
        {
            // Same reasoning as RescanAsync: the check at the top of this method is an early out,
            // and a batch admitted after DisposeAsync set the flag would mutate a released block.
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshot = CurrentSnapshot;
            var driveBlock = snapshot.GetDriveBlock(driveOrdinal);
            if (driveBlock.ProducerKind != ProducerKind.Mft)
            {
                throw new InvalidOperationException(
                    $"Drive {driveLetter} was indexed by an enumeration producer and does not support journal mutation.");
            }

            var writer = new BlockWriter(driveBlock.Block);
            var mutator = new JournalMutator(writer);
            return mutator.Apply(snapshot, driveOrdinal, entries, journalId, nextUsn);
        }
        finally
        {
            _swapGate.Release();
        }
    }

    /// <summary>
    ///     Delivers every change to every current subscriber, isolating one handler's failure
    ///     from another's: a snapshot of the invocation list is taken once, and each handler is
    ///     invoked for every change even if that handler (or another one) already threw for an
    ///     earlier change, so one bad subscriber never starves the rest of the batch. Every
    ///     collected exception is surfaced only after delivery has fully finished, since by then
    ///     the mutation this batch made is already durable and nothing further depends on this
    ///     call returning normally.
    /// </summary>
    void RaiseChanged(IReadOnlyList<FileChange> changes)
    {
        var subscribers = Changed;
        if (subscribers is null)
        {
            return;
        }

        List<Exception>? handlerExceptions = null;
        foreach (var change in changes)
        {
            foreach (var handler in subscribers.GetInvocationList())
            {
                try
                {
                    ((Action<FileChange>)handler)(change);
                }
                catch (Exception exception)
                {
                    (handlerExceptions ??= []).Add(exception);
                }
            }
        }

        if (handlerExceptions is not { Count: > 0 })
        {
            return;
        }

        if (handlerExceptions.Count > 1)
        {
            throw new AggregateException(handlerExceptions);
        }

        // Rethrowing the caught instance directly would overwrite its stack trace with this
        // throw site inside MFTLib, costing a consumer the frame in their own handler that
        // actually threw. Capturing preserves it.
        ExceptionDispatchInfo.Capture(handlerExceptions[0]).Throw();
    }
}
