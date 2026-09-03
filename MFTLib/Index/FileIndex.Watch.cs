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
    ///     Validates that this index is usable and that the caller has not already cancelled, then
    ///     completes. It starts nothing, because no producer in this build supports a live watch:
    ///     the enumeration producer has no journal cursor, and
    ///     <see cref="DriveStatus.WatchSupported" /> is therefore false for every drive here. The
    ///     call is accepted rather than rejected so a caller's startup sequence needs no version
    ///     check once the MFT producer arrives and brings the live watch with it. Until then, drive
    ///     contents change only through <see cref="ApplyJournalEntries" /> and
    ///     <see cref="RescanAsync" />.
    /// </summary>
    public Task StartWatchingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Applies one journal batch to a drive's block in place and raises
    ///     <see cref="Changed" /> for each applied change. This is the seam the watch pipeline
    ///     drives; it returns the batch so a caller can act on it without subscribing.
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

        IReadOnlyList<FileChange> changes;
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
            changes = mutator.Apply(snapshot, driveOrdinal, entries, journalId, nextUsn);
        }
        finally
        {
            _swapGate.Release();
        }

        RaiseChanged(changes);
        return changes;
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
