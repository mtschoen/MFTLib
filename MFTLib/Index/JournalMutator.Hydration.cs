namespace MFTLib.Index;

/// <summary>How the mutator writes the row of a record the index never had.</summary>
sealed partial class JournalMutator
{
    /// <summary>
    ///     Writes the row of a record whose row the index never had. A refusal is returned as the outcome and writes
    ///     nothing; otherwise the outcome is applied with no change and <paramref name="hydrated" /> says whether a row
    ///     was written.
    /// </summary>
    ApplyOutcome TryHydrateRow(UsnJournalEntry entry, uint rowIndex, out bool hydrated) =>
        TryHydrateRow(entry, rowIndex, entry.FileName, entry.ParentRecordNumber, out hydrated);

    /// <summary>As the record's own hydration, with <paramref name="name" /> under <paramref name="parentRecordNumber" />.</summary>
    ApplyOutcome TryHydrateRow(UsnJournalEntry entry, uint rowIndex, string name, ulong parentRecordNumber,
        out bool hydrated)
    {
        hydrated = false;
        if ((Writer.ReadRowFlags(rowIndex) & RowFlags.InUse) != 0)
        {
            return ApplyOutcome.Applied(null);
        }

        if (parentRecordNumber > uint.MaxValue)
        {
            return ApplyOutcome.Refused(WriteRefusal.Slots, growable: false);
        }

        var columns = BuildColumns(entry, (uint)parentRecordNumber);
        var refusal = Writer.TryWriteRow(rowIndex, name, in columns);
        hydrated = refusal == WriteRefusal.None;
        return refusal == WriteRefusal.None ? ApplyOutcome.Applied(null) : ApplyOutcome.Refused(refusal);
    }

    static RowColumns BuildColumns(UsnJournalEntry entry, uint parentRow)
    {
        var flags = RowFlags.InUse | RowFlags.SizeUnknown | RowFlags.LastWriteTimeUnknown;
        if ((entry.FileAttributes & FileAttributes.Directory) != 0)
        {
            flags |= RowFlags.Directory;
        }

        return new RowColumns(parentRow, flags, (uint)entry.FileAttributes, Size: 0, ModifiedTicks: 0,
            entry.SequenceNumber);
    }
}
