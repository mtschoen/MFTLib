namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     Applies entries to the volume's current block under its write gate. A cancelled
        ///     <paramref name="cancellationToken" /> ends the wait for the gate; once the gate is
        ///     held the batch applies in full.
        /// </summary>
        public async Task<IReadOnlyList<RoutedChanges>> ApplyJournalEntriesCoreAsync(WatchInstance? instance,
            IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsClosing, typeof(FileIndex));
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

                ObjectDisposedException.ThrowIf(IsClosing, typeof(FileIndex));
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
                var changes = mutator.Apply(rootBlock, entries, journalIdentifier, nextUsn);
                // Still under the write gate: a directory view follows its directory through renames, deletions and
                // re-creations by rechecking its binding once per applied batch.
                foreach (var view in ViewsSnapshot())
                {
                    if (view.Block is { } bound)
                    {
                        bound.View.RefreshAfterBatch(bound, entries);
                    }
                }
                return RouteLocked(changes);
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
