using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Raised once per applied change, in the order the journal batch delivered them, to
    ///     every subscriber, even when an earlier subscriber threw for an earlier change (or for
    ///     this one). The mutation and the USN cursor are already durable by the time any handler
    ///     runs: <see cref="ApplyJournalEntries" /> applies the whole batch and releases its drive's
    ///     write gate before raising this event at all, so a throwing handler never undoes anything
    ///     and never stops another handler from seeing the rest of the batch. A close record that
    ///     repeats only reasons its open cycle already reported applies its metadata to the
    ///     block without raising this event, so within one block a transition raises one change even
    ///     though NTFS writes at least two journal records for it. See
    ///     <see cref="ApplyJournalEntries" /> for how a handler exception is surfaced to the
    ///     caller. A rescan can publish before an old change is delivered, and its new watch can
    ///     repeat that change during catch-up. Queries can lag until that watch is CaughtUp.
    ///     A handler must not block on a lifecycle call of this index: see
    ///     <see cref="WatchFaulted" />.
    /// </summary>
    public event Action<FileChange>? Changed;

    /// <summary>
    ///     Raised when a drive's watch first sees a subscriber fault, every time a drive's watch
    ///     ends with a drive, apply, or channel fault, and after every scan of a drive whose
    ///     journal catch-up was lost or whose replacement watch could not start. Every fault names
    ///     its drive. Raised from that drive's pump, or for <see cref="WatchFaultKind.CatchUpLost" />
    ///     and <see cref="WatchFaultKind.RescanRestart" /> from the scan after publication drained
    ///     the old pump, so faults of different drives may be raised
    ///     concurrently while one drive's faults never overlap. The scan operation raises its
    ///     fault while it still holds the drive's lifecycle gate, so a handler must queue, not
    ///     wait for, a rescan or start of that drive. Exceptions thrown by fault handlers are
    ///     discarded.
    ///     <para>
    ///         Inside any <see cref="Changed" /> or <see cref="WatchFaulted" /> handler of this index,
    ///         whichever drive it concerns, <c>StartWatchingAsync</c>, <c>StopWatchingAsync</c>,
    ///         <c>RescanAsync</c>, <c>DisposeAsync</c>, their batched forms and a
    ///         <c>WaitForCatchUpAsync</c> that has not yet settled fail at once with
    ///         <see cref="InvalidOperationException" />, because each can wait for a pump that is itself
    ///         blocked in a handler. The rejection also covers work the handler starts and that runs
    ///         before the handler returns. Queue such a call to run after the handler returns, for
    ///         example with <see cref="Task.Run(Action)" />. Queries, <see cref="Drives" />, and a wait
    ///         that has already settled are allowed.
    ///     </para>
    /// </summary>
    public event Action<WatchFault>? WatchFaulted;

    /// <summary>
    ///     A test seam: invoked with the drive letter on entry to a watch pump's apply, before the
    ///     drive's write gate is taken, so a test can hold a pump in the middle of applying a batch
    ///     it has already read.
    /// </summary>
    internal Action<char>? ApplyJournalEntriesEnteredForTest { get; set; }

    /// <summary>
    ///     A test seam: invoked with the drive letter while an apply holds its drive's write gate,
    ///     after the batch passed its instance check and read the current snapshot, and before the
    ///     block is mutated.
    /// </summary>
    internal Action<char>? ApplyJournalEntriesInsideWriteGateForTest { get; set; }

    /// <summary>
    ///     Applies one journal batch to a drive's block in place and raises
    ///     <see cref="Changed" /> for each applied change. This is the seam the watch pipeline
    ///     drives; it returns the batch so a caller can act on it without subscribing. A drive's
    ///     watch pump applies through the same path and raises <see cref="Changed" /> itself, so
    ///     it can tell an apply failure from a subscriber failure.
    /// </summary>
    /// <remarks>
    ///     The mutation runs under the drive's write gate, the same gate a rescan of that drive
    ///     holds while it commits the drive's new block, so a commit and a journal batch can never
    ///     touch the same drive's block at once: a rescan builds an entirely new block file and
    ///     only publishes it under the gate, and a journal batch takes its snapshot and its
    ///     <see cref="BlockWriter" /> under the same gate, so it always mutates the drive's block
    ///     that is current once it is its turn, never one a commit is about to supersede. Only
    ///     this drive's gate is taken: a batch never waits for another drive's commit or batch. A
    ///     commit for another drive may retire the snapshot this batch read while it runs, which is
    ///     safe: this drive's block is the same instance in both snapshots, and the handles in the
    ///     batch's changes keep the snapshot they came from alive. The gate is released before
    ///     <see cref="Changed" /> is raised, so a subscriber's handler never runs while a commit
    ///     is blocked waiting on this call.
    /// </remarks>
    public IReadOnlyList<FileChange> ApplyJournalEntries(char driveLetter,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalId, long nextUsn)
    {
        var changes = ApplyJournalEntriesCore(driveLetter, instance: null, entries, journalId, nextUsn);
        RaiseChanged(changes);
        return changes;
    }

    /// <summary>
    ///     Everything <see cref="ApplyJournalEntries" /> does up to and including releasing the
    ///     drive's write gate, without raising <see cref="Changed" />. A pump passes its
    ///     <paramref name="instance" />: once the gate is held, the batch is applied only while
    ///     that instance is still the drive's current, running watch and the block it was armed
    ///     from is still published, and is otherwise dropped, returning no changes. A retiring pump's
    ///     last batch therefore never reaches a successor's block.
    /// </summary>
    IReadOnlyList<FileChange> ApplyJournalEntriesCore(char driveLetter, WatchInstance? instance,
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

        if (instance is not null)
        {
            ApplyJournalEntriesEnteredForTest?.Invoke(upperDriveLetter);
        }

        // ApplyJournalEntries is synchronous by its public contract (it returns
        // IReadOnlyList<FileChange>, not a Task), so this blocks on the SemaphoreSlim itself,
        // not on a Task: it is the synchronous counterpart to the commit's WaitAsync, not
        // sync-over-async. An automated scanner can mistake any ".Wait()" call for blocking on
        // a Task; this one is not.
        var writeGate = GetDriveRuntime(upperDriveLetter).WriteGate;
        writeGate.Wait();
        try
        {
            if (instance is not null && !IsRunningWatchOverItsBlock(upperDriveLetter, instance))
            {
                return [];
            }

            // The check at the top of this method is an early out. Disposal takes this gate before
            // it releases the snapshots, so a batch admitted after it set the flag must not start
            // a mutation that disposal would then have to wait for.
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshot = CurrentSnapshot;
            var driveBlock = snapshot.GetDriveBlock(driveOrdinal);
            if (driveBlock.ProducerKind != ProducerKind.Mft)
            {
                throw new InvalidOperationException(
                    $"Drive {driveLetter} was indexed by an enumeration producer and does not support journal mutation.");
            }

            ApplyJournalEntriesInsideWriteGateForTest?.Invoke(upperDriveLetter);
            var writer = new BlockWriter(driveBlock.Block);
            var mutator = new JournalMutator(writer);
            return mutator.Apply(snapshot, driveOrdinal, entries, journalId, nextUsn);
        }
        finally
        {
            writeGate.Release();
        }
    }

    bool IsRunningWatchOverItsBlock(char driveLetter, WatchInstance instance)
    {
        lock (_stateLock)
        {
            return instance.State == WatchInstanceState.Running &&
                   IsCurrentWatchOverItsBlockLocked(_driveRuntimes[driveLetter], instance);
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
                    Deliver((Action<FileChange>)handler, change);
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

        if (handlerExceptions.Count == 1)
        {
            // Rethrowing the caught instance directly would overwrite its stack trace with this
            // throw site inside MFTLib, costing a consumer the frame in their own handler that
            // actually threw. Capturing preserves it.
            ExceptionDispatchInfo.Capture(handlerExceptions[0]).Throw();
        }

        throw new AggregateException(handlerExceptions);
    }
}
