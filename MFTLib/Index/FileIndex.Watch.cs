namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal IReadOnlyList<FileChange> ApplyJournalEntries(DriveKey drive,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        var runtime = GetDriveRuntime(drive);
        var changes = ApplyJournalEntriesCore(drive, instance: null, entries, journalIdentifier, nextUsn);
        runtime.RaiseChanged(changes);
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
    IReadOnlyList<FileChange> ApplyJournalEntriesCore(DriveKey drive, WatchInstance? instance,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entries);
        var runtime = GetDriveRuntime(drive);
        var validatedDrive = runtime.Drive;

        if (!TryGetDriveOrdinal(drive, out var driveOrdinal))
        {
            throw new InvalidOperationException(
                $"Drive {drive} is offline and has no block to apply journal entries to.");
        }

        if (instance is not null)
        {
            Hooks.ApplyJournalEntriesEntered?.Invoke(validatedDrive);
        }

        // ApplyJournalEntries is synchronous by its public contract (it returns
        // IReadOnlyList<FileChange>, not a Task), so this blocks on the SemaphoreSlim itself,
        // not on a Task: it is the synchronous counterpart to the commit's WaitAsync, not
        // sync-over-async. An automated scanner can mistake any ".Wait()" call for blocking on
        // a Task; this one is not.
        var writeGate = runtime.WriteGate;
        writeGate.Wait();
        try
        {
            if (instance is not null && !IsRunningWatchOverItsBlock(validatedDrive, instance))
            {
                return [];
            }

            // The check at the top of this method is an early out. Disposal takes this gate before
            // it releases the snapshots, so a batch admitted after it set the flag must not start
            // a mutation that disposal would then have to wait for.
            ObjectDisposedException.ThrowIf(_disposed, this);
            var driveBlock = CurrentSnapshot[driveOrdinal];
            if (driveBlock.ProducerKind != ProducerKind.Mft)
            {
                throw new InvalidOperationException(
                    $"Drive {drive} was indexed by an enumeration producer and does not support journal mutation.");
            }

            Hooks.ApplyJournalEntriesInsideWriteGate?.Invoke(validatedDrive);
            var writer = new BlockWriter(driveBlock.Block);
            var mutator = new JournalMutator(writer);
            return mutator.Apply(driveBlock, entries, journalIdentifier, nextUsn);
        }
        finally
        {
            writeGate.Release();
        }
    }

    bool IsRunningWatchOverItsBlock(DriveKey drive, WatchInstance instance)
    {
        lock (_stateLock)
        {
            return instance.State == WatchInstanceState.Running &&
                   IsCurrentWatchOverItsBlockLocked(_driveRuntimes[drive], instance);
        }
    }
}
