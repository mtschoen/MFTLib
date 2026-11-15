namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     Applies entries to the volume's current block under its write gate. A record the block cannot store
        ///     stops the apply there; when a larger block can hold it the block grows (docs/watch-lifetime.md,
        ///     "Growth") and the apply resumes at that record on the replacement, with the same root block. A record
        ///     no growth can store is flagged and skipped. A cancelled <paramref name="cancellationToken" /> ends the
        ///     wait for the gate; once the gate is held the batch applies in full, except that a stop or disposal
        ///     during a growth ends it at the refused record, whose cursor the block then holds. While a scan
        ///     produces this root's replacement a growth parks instead: the changes applied so far go to
        ///     <paramref name="deliverBeforePark" /> with no gate or lock held, the apply waits for the scan to
        ///     publish or give up, and then resumes only if the same watch still runs over the same lineage.
        /// </summary>
        /// <returns>The changes applied after the last park, routed to their views.</returns>
        public async Task<IReadOnlyList<RoutedChanges>> ApplyJournalEntriesCoreAsync(WatchInstance? instance,
            IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn,
            Action<IReadOnlyList<RoutedChanges>> deliverBeforePark, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsClosing, typeof(FileIndex));
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(deliverBeforePark);

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

            var batch = new BatchApplication(entries, journalIdentifier, nextUsn);
            MappedBlock? lineage = null;
            while (true)
            {
                await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                Task? park;
                IReadOnlyList<RoutedChanges> routed;
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

                    // A parked batch resumes only on the lineage it parked on: a published rescan replays from its own cursor.
                    if (lineage is not null && !ReferenceEquals(lineage, rootBlock.Mapped))
                    {
                        return [];
                    }

                    lineage = rootBlock.Mapped;
                    if (rootBlock.ProducerKind != ProducerKind.Mft)
                    {
                        throw new InvalidOperationException(
                            $"Volume {Root} was indexed by an enumeration producer and does not support journal mutation.");
                    }

                    Hooks.ApplyJournalEntriesInsideWriteGate?.Invoke(Root);
                    var changes = ApplyUntilParked(rootBlock, batch, cancellationToken, out park);
                    // Still under the write gate: a directory view follows its directory through renames, deletions and
                    // re-creations by rechecking its binding once per applied batch.
                    foreach (var view in ViewsSnapshot())
                    {
                        if (view.Block is { } bound)
                        {
                            bound.View.RefreshAfterBatch(bound, entries);
                        }
                    }

                    routed = RouteLocked(changes);
                }
                finally
                {
                    WriteGate.Release();
                }

                if (park is null)
                {
                    return routed;
                }

                deliverBeforePark(routed);
                await park.WaitAsync(cancellationToken).ConfigureAwait(false);
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
