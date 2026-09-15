namespace MFTLib.Index;

/// <summary>
///     The reason bits already reported for each row's current open cycle on one drive
///     block. NTFS ends every open cycle with a close record that repeats the cycle's
///     reasons plus <see cref="UsnReason.Close" />; this state is what lets
///     <see cref="JournalMutator" /> report the first record of a cycle and coalesce the
///     close record instead of emitting the same change twice. Only rows with an open
///     cycle occupy an entry, and a close record removes its row, so the dictionary stays
///     small no matter how large the block is. Runtime-only state: it is not persisted in
///     the block and dies with the <see cref="DriveBlock" /> that owns it, which is the
///     lifecycle reset a rescan (block replacement) provides. Not thread-safe: every
///     journal mutation is already serialized by the index-wide swap gate
///     (<see cref="FileIndex.ApplyJournalEntries" />), and tests drive it single-threaded.
/// </summary>
internal sealed class ReportedReasonCycles
{
    readonly Dictionary<uint, OpenCycle> _openCycles = new();

    struct OpenCycle
    {
        public ushort SequenceNumber;
        public UsnReason ReportedReasons;
    }

    /// <summary>
    ///     The reasons reported for this row since its last close record. A sequence
    ///     number that does not match the tracked cycle means the MFT segment was reused
    ///     since the cycle opened: nothing reported for the old record carries over, and
    ///     the stale cycle is dropped.
    /// </summary>
    public UsnReason GetReportedReasons(uint rowIndex, ushort sequenceNumber)
    {
        if (!_openCycles.TryGetValue(rowIndex, out var cycle))
        {
            return UsnReason.None;
        }

        if (cycle.SequenceNumber != sequenceNumber)
        {
            _openCycles.Remove(rowIndex);
            return UsnReason.None;
        }

        return cycle.ReportedReasons;
    }

    /// <summary>Records reasons just reported for a row's open cycle.</summary>
    public void MarkReported(uint rowIndex, ushort sequenceNumber, UsnReason reasons)
    {
        if (_openCycles.TryGetValue(rowIndex, out var cycle) && cycle.SequenceNumber == sequenceNumber)
        {
            cycle.ReportedReasons |= reasons;
            _openCycles[rowIndex] = cycle;
            return;
        }

        _openCycles[rowIndex] = new OpenCycle { SequenceNumber = sequenceNumber, ReportedReasons = reasons };
    }

    /// <summary>A close record ends the row's open cycle.</summary>
    public void CloseCycle(uint rowIndex)
    {
        _openCycles.Remove(rowIndex);
    }
}
