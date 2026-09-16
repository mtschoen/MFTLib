namespace MFTLib.Index;

/// <summary>
///     The reason bits already reported for each row's current open cycle on one drive
///     block, plus the name and parent the cycle's reported rename carried. NTFS ends every
///     open cycle with a close record that repeats the cycle's reasons plus
///     <see cref="UsnReason.Close" />; this state is what lets <see cref="JournalMutator" />
///     report the first record of a cycle and coalesce the close record instead of emitting
///     the same change twice. A repeated reason bit is suppressed only as the echo of what
///     the cycle already applied: a rename is keyed on its name and parent, because NTFS
///     writes one record pair per rename and does not require a close between renames, so a
///     second rename inside one open cycle is a new transition, not an echo. Only rows with
///     an open cycle occupy an entry, and a close record removes its row, so the dictionary
///     stays small no matter how large the block is. Runtime-only state: it is not persisted
///     in the block and dies with the <see cref="DriveBlock" /> that owns it, which is the
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
        public string? RenameName;
        public ulong RenameParent;
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

    /// <summary>
    ///     Records reasons just reported for a row's open cycle. When the record carried
    ///     <see cref="UsnReason.RenameNewName" />, its file name and parent replace the
    ///     cycle's rename payload, so a later record is compared against the latest applied
    ///     rename.
    /// </summary>
    public void MarkReported(uint rowIndex, ushort sequenceNumber, UsnReason reasons,
        string renameName, ulong renameParent)
    {
        var carriesRename = (reasons & UsnReason.RenameNewName) != 0;
        if (_openCycles.TryGetValue(rowIndex, out var cycle) && cycle.SequenceNumber == sequenceNumber)
        {
            cycle.ReportedReasons |= reasons;
            if (carriesRename)
            {
                cycle.RenameName = renameName;
                cycle.RenameParent = renameParent;
            }

            _openCycles[rowIndex] = cycle;
            return;
        }

        _openCycles[rowIndex] = new OpenCycle
        {
            SequenceNumber = sequenceNumber,
            ReportedReasons = reasons,
            RenameName = carriesRename ? renameName : null,
            RenameParent = carriesRename ? renameParent : 0
        };
    }

    /// <summary>
    ///     True when a <see cref="UsnReason.RenameNewName" /> record only repeats the rename
    ///     this row's open cycle already reported: same name, same parent. That is what the
    ///     close record (or a cumulative intermediate record) carries once the rename was
    ///     applied. A record carrying a different name or parent is another rename of the
    ///     same open handle and must classify again.
    /// </summary>
    public bool IsReportedRenameEcho(uint rowIndex, ushort sequenceNumber, string renameName,
        ulong renameParent)
    {
        if (!_openCycles.TryGetValue(rowIndex, out var cycle)
            || cycle.SequenceNumber != sequenceNumber
            || (cycle.ReportedReasons & UsnReason.RenameNewName) == 0)
        {
            return false;
        }

        return string.Equals(cycle.RenameName, renameName, StringComparison.Ordinal)
            && cycle.RenameParent == renameParent;
    }

    /// <summary>A close record ends the row's open cycle.</summary>
    public void CloseCycle(uint rowIndex)
    {
        _openCycles.Remove(rowIndex);
    }
}
