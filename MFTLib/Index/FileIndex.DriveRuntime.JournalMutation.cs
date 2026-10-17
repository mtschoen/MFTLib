namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        public IReadOnlyList<FileChange> ApplyJournalEntriesCore(WatchInstance? instance,
            IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, _index);
            ArgumentNullException.ThrowIfNull(entries);

            lock (StateLock)
            {
                if (Block is null)
                {
                    throw new InvalidOperationException(
                        $"Drive {Drive} is offline and has no block to apply journal entries to.");
                }
            }

            if (instance is not null)
            {
                Hooks.ApplyJournalEntriesEntered?.Invoke(Drive);
            }

            // ApplyJournalEntries is synchronous by its public contract (it returns
            // IReadOnlyList<FileChange>, not a Task), so this blocks on the SemaphoreSlim itself,
            // not on a Task: it is the synchronous counterpart to the commit's WaitAsync, not
            // sync-over-async.
            // aislop-ignore-next-line csharp-sync-over-async -- WriteGate is a SemaphoreSlim, not a Task; ApplyJournalEntries is synchronous by its public contract
            WriteGate.Wait();
            try
            {
                if (instance is not null && !IsRunningWatchOverItsBlock(instance))
                {
                    return [];
                }

                ObjectDisposedException.ThrowIf(IsDisposed, _index);
                DriveBlock driveBlock;
                lock (StateLock)
                {
                    driveBlock = Block ?? throw new ObjectDisposedException(nameof(FileIndex),
                        "The index is being disposed.");
                }

                if (driveBlock.ProducerKind != ProducerKind.Mft)
                {
                    throw new InvalidOperationException(
                        $"Drive {Drive} was indexed by an enumeration producer and does not support journal mutation.");
                }

                Hooks.ApplyJournalEntriesInsideWriteGate?.Invoke(Drive);
                var writer = new BlockWriter(driveBlock.Block);
                var mutator = new JournalMutator(writer);
                return mutator.Apply(driveBlock, entries, journalIdentifier, nextUsn);
            }
            finally
            {
                WriteGate.Release();
            }
        }

        bool IsRunningWatchOverItsBlock(WatchInstance instance)
        {
            lock (StateLock)
            {
                return instance.State == WatchInstanceState.Running &&
                       IsCurrentWatchOverItsBlockLocked(instance);
            }
        }
    }
}
