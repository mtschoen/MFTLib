namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Applies entries to the drive's current block under its write gate. A cancelled
        ///     <paramref name="cancellationToken" /> ends the wait for the gate; once the gate is
        ///     held the batch applies in full.
        /// </summary>
        public async Task<IReadOnlyList<FileChange>> ApplyJournalEntriesCoreAsync(WatchInstance? instance,
            IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn,
            CancellationToken cancellationToken)
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

            await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
