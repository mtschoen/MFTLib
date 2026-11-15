namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     Where one journal batch stands across the growths and parks of its application: the next record to
        ///     apply, the refusal the latest growth remedied, the growth failure that ends growing for the rest of
        ///     the batch, and the first record the batch skipped.
        /// </summary>
        sealed class BatchApplication(IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
        {
            public IReadOnlyList<UsnJournalEntry> Entries { get; } = entries;
            public ulong JournalIdentifier { get; } = journalIdentifier;
            public long NextUsn { get; } = nextUsn;

            /// <summary>
            ///     The lineage the batch first skipped a record on, with that skip's cause; null before any. A watch
            ///     raises <see cref="WatchFaultKind.CapacityExhausted" /> with it once per lineage.
            /// </summary>
            public CapacityExhaustion? FirstSkip { get; private set; }

            /// <summary>Records a skip on <paramref name="lineage" /> for <paramref name="cause" /> when it is the batch's first.</summary>
            public void NoteSkip(MappedBlock lineage, Exception cause) => FirstSkip ??= new CapacityExhaustion(lineage, cause);

            /// <summary>Position in <see cref="Entries" /> where a resumed apply starts; the count once the batch ended.</summary>
            public int NextIndex { get; set; }

            /// <summary>Which record, refused by which region, the most recent growth was for; null before any.</summary>
            public (int Index, WriteRefusal Reason)? LastGrowth { get; set; }

            /// <summary>
            ///     The I/O error the batch's one growth attempt failed with; later refusals in the batch are skipped with
            ///     it, and the next refusal in a later batch tries again.
            /// </summary>
            public Exception? GrowthFailure { get; set; }

            /// <summary>Set when a stop or disposal ended a growth: the batch ends at the refused record.</summary>
            public bool Stopped { get; set; }

            /// <summary>
            ///     The cause that makes <paramref name="refusal" /> unstorable, or null when a growth can make room: the
            ///     record is out of range, the same record was refused by the same region right after a growth, so
            ///     growing again cannot help, the lineage already found that region at its clamp, or a growth already
            ///     failed in this batch.
            /// </summary>
            public Exception? WhyNotGrowable(JournalRefusal refusal, LineageCapacity lineage)
            {
                var entry = Entries[refusal.Index];
                if (!refusal.Growable)
                {
                    return new InvalidOperationException(FormattableString.Invariant(
                        $"Record {entry.RecordNumber} at USN {entry.Usn} names a record or parent number above 32 bits, which no block can store."));
                }

                if (LastGrowth == (refusal.Index, refusal.Reason))
                {
                    return new InvalidOperationException(FormattableString.Invariant(
                        $"Record {entry.RecordNumber} at USN {entry.Usn} was refused by the {refusal.Reason} region again right after a growth for it."));
                }

                return lineage.ClampFor(refusal.Reason) ?? GrowthFailure;
            }
        }

        /// <summary>
        ///     Applies <paramref name="batch" /> from its next record to its end, growing the block at each growable
        ///     refusal and skipping each record that cannot be stored (the first skip is kept as
        ///     <see cref="BatchApplication.FirstSkip" />). Stops early to park, with
        ///     <paramref name="park" /> set to the production marker, when a scan is producing this root's replacement,
        ///     or when a stop or disposal ended a growth. The caller holds <see cref="WriteGate" />.
        /// </summary>
        List<FileChange> ApplyUntilParked(RootBlock rootBlock, BatchApplication batch,
            CancellationToken cancellationToken, out Task? park)
        {
            park = null;
            var changes = new List<FileChange>();
            while (true)
            {
                var mutator = new JournalMutator(new BlockWriter(rootBlock.Block));
                var result = mutator.ApplyUntilRefusal(rootBlock, batch.Entries, batch.NextIndex, batch.JournalIdentifier,
                    batch.NextUsn);
                changes.AddRange(result.Changes);
                if (result.Refusal is not { } refusal)
                {
                    batch.NextIndex = batch.Entries.Count;
                    return changes;
                }

                batch.NextIndex = refusal.Index;
                var cause = batch.WhyNotGrowable(refusal, rootBlock.Mapped.Capacity);
                if (cause is null)
                {
                    if (TryGetProductionMarker(out var marker))
                    {
                        park = marker;
                        return changes;
                    }

                    cause = TryGrow(rootBlock, batch, refusal, cancellationToken);
                    if (batch.Stopped)
                    {
                        return changes;
                    }
                }

                if (cause is not null)
                {
                    SkipUnstorableRecord(rootBlock, batch.Entries[refusal.Index], refusal, cause);
                    batch.NoteSkip(rootBlock.Mapped, cause);
                    batch.NextIndex = refusal.Index + 1;
                }
            }
        }

        /// <summary>
        ///     Grows the block for <paramref name="refusal" />. Returns null when the replacement is published or a stop
        ///     ended the growth (<see cref="BatchApplication.Stopped" />), otherwise the failure: the planner's clamp,
        ///     which the lineage remembers for a name region (<see cref="LineageCapacity" />), or the I/O error that
        ///     stopped the copy, which ends growing for the rest of the batch only. A lineage
        ///     released before publication (<see cref="ObjectDisposedException" /> from <see cref="MappedBlock.Replace" />)
        ///     is a stop like disposal: it never flags the block or skips the record.
        /// </summary>
        Exception? TryGrow(RootBlock rootBlock, BatchApplication batch, JournalRefusal refusal,
            CancellationToken cancellationToken)
        {
            var entry = batch.Entries[refusal.Index];
            var source = rootBlock.Block;
            BlockCapacities capacities;
            try
            {
                capacities = WatchGrowthPlan.Plan(WatchGrowthPlan.CapacitiesOf(source), source.RowCount,
                    refusal.Reason, (uint)entry.RecordNumber);
            }
            catch (InvalidOperationException clamp)
            {
                if (refusal.Reason != WriteRefusal.Slots)
                {
                    rootBlock.Mapped.Capacity.RememberClamp(refusal.Reason, clamp);
                }

                return clamp;
            }

            try
            {
                GrowPublishedBlock(rootBlock, capacities, refusal, entry, cancellationToken);
                batch.LastGrowth = (refusal.Index, refusal.Reason);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsClosing)
            {
                batch.Stopped = true;
                return null;
            }
            catch (ObjectDisposedException)
            {
                batch.Stopped = true;
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                batch.GrowthFailure = exception;
                return exception;
            }
        }

        /// <summary>
        ///     The one place a record the block cannot store is given up: the block is flagged capacity-exhausted, the
        ///     record is skipped whole, and one diagnostics line names it and <paramref name="cause" />.
        /// </summary>
        void SkipUnstorableRecord(RootBlock rootBlock, UsnJournalEntry entry, JournalRefusal refusal, Exception cause)
        {
            new BlockWriter(rootBlock.Block).MarkCapacityExhausted();
            DiagnosticsReport.Send(Diagnostics, FormattableString.Invariant(
                $"Skipped journal record at USN {entry.Usn} (record {entry.RecordNumber}): refused by the {refusal.Reason} region and the block could not grow: {cause.Message}"));
        }
    }
}
