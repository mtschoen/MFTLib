namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class RootRuntime
    {
        /// <summary>
        ///     Applies entries to the volume's current block under its write gate. A cancelled
        ///     <paramref name="cancellationToken" /> ends the wait for the gate; once the gate is
        ///     held the batch applies in full.
        /// </summary>
        public async Task<IReadOnlyList<FileChange>> ApplyJournalEntriesCoreAsync(WatchInstance? instance,
            IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, Index);
            ArgumentNullException.ThrowIfNull(entries);

            lock (StateLock)
            {
                if (Block is null)
                {
                    throw new InvalidOperationException(
                        $"Volume {Root} is offline and has no block to apply journal entries to.");
                }
            }

            if (instance is not null)
            {
                Hooks.ApplyJournalEntriesEntered?.Invoke(Root);
            }

            await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (instance is not null && !IsRunningWatchOverItsBlock(instance))
                {
                    return [];
                }

                ObjectDisposedException.ThrowIf(IsDisposed, Index);
                RootBlock rootBlock;
                lock (StateLock)
                {
                    rootBlock = Block ?? throw new ObjectDisposedException(nameof(FileIndex),
                        "The index is being disposed.");
                }

                if (rootBlock.ProducerKind != ProducerKind.Mft)
                {
                    throw new InvalidOperationException(
                        $"Volume {Root} was indexed by an enumeration producer and does not support journal mutation.");
                }

                Hooks.ApplyJournalEntriesInsideWriteGate?.Invoke(Root);
                var writer = new BlockWriter(rootBlock.Block);
                var mutator = new JournalMutator(writer);
                return mutator.Apply(rootBlock, entries, journalIdentifier, nextUsn);
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
