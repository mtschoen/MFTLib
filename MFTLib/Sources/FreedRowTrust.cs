using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Decides which freed rows of a scan keep their parent. A freed record's parent column is only a hint: NTFS
///     reuses record numbers, so the directory it names may now be someone else. A freed row is trusted when every
///     hop of its chain reaches record 5 and each hop's parent is a directory whose stored sequence equals the
///     sequence the child's name referenced, or exactly one higher when the parent was freed too (NTFS bumps the
///     sequence when it frees a record, and 16-bit arithmetic wraps). The walk is at most
///     <see cref="BlockLayout.MaximumPathDepth" /> components, never revisits a record and rebuilds to a path of at
///     most <see cref="BlockLayout.MaximumPathUnits" /> UTF-16 units. A freed row that fails the rule is detached.
/// </summary>
sealed class FreedRowTrust
{
    const uint RootRecord = 5;

    readonly ushort[] _parentSequences;
    readonly List<uint> _freedRows = [];
    RecordFacts? _rootRecord;

    /// <summary>Sizes the per-row parent sequences from the block's own slot capacity.</summary>
    public FreedRowTrust(BlockFile block)
    {
        Block = block;
        _parentSequences = new ushort[block.SlotCapacity];
    }

    BlockFile Block { get; }

    /// <summary>What the rule needs to know about a record that is a hop's parent.</summary>
    readonly record struct RecordFacts(bool IsDirectory, bool IsFreed, ushort Sequence);

    /// <summary>
    ///     Remembers record 5 even when it gets no row, because the rule asks the root only for its kind and sequence,
    ///     never for a name.
    /// </summary>
    public void Observe(MftPackedEntry entry)
    {
        if (entry.RecordNumber == RootRecord)
        {
            _rootRecord = new RecordFacts(entry.IsDirectory, !entry.InUse, entry.SequenceNumber);
        }
    }

    /// <summary>Records what the rule needs of a record that was written as a row.</summary>
    public void Wrote(MftPackedEntry entry)
    {
        var row = (uint)entry.RecordNumber;
        _parentSequences[row] = entry.ParentSequenceNumber;
        if (!entry.InUse)
        {
            _freedRows.Add(row);
        }
    }

    /// <summary>
    ///     Applies the rule to every freed row once the last slice is written, since a parent can arrive after its
    ///     child. Every decision is made on the rows as scanned, then the untrusted rows are detached together.
    /// </summary>
    public void DetachUntrusted(BlockWriter writer, CancellationToken cancellationToken)
    {
        var untrusted = new List<uint>();
        foreach (var row in _freedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTrusted(row))
            {
                untrusted.Add(row);
            }
        }

        foreach (var row in untrusted)
        {
            writer.DetachRow(row);
        }
    }

    bool IsTrusted(uint origin)
    {
        // A freed root record is no root: nothing above it verifies it.
        if (origin == RootRecord)
        {
            return false;
        }

        var visited = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        long units = 0;
        var current = origin;
        var rowCount = Block.RowCount;
        while (current != RootRecord && current < rowCount && visited.Count < BlockLayout.MaximumPathDepth)
        {
            if (visited.Contains(current))
            {
                break;
            }

            visited.Add(current);
            var nameLength = NamePool.ReadRowName(Block, current).Length;
            if (nameLength == 0)
            {
                break;
            }

            units += nameLength;
            var parent = Block.Rows[(int)current].ParentRow;
            if (!TrustsParentHop(current, parent))
            {
                return false;
            }

            current = parent;
        }

        // The separators between the components are part of the path.
        return current == RootRecord && units + visited.Count - 1 <= BlockLayout.MaximumPathUnits;
    }

    bool TrustsParentHop(uint child, uint parent)
    {
        if (!TryGetFacts(parent, out var facts) || !facts.IsDirectory)
        {
            return false;
        }

        var referencedSequence = _parentSequences[child];
        return facts.Sequence == referencedSequence ||
               (facts.IsFreed && facts.Sequence == (ushort)(referencedSequence + 1));
    }

    bool TryGetFacts(uint record, out RecordFacts facts)
    {
        if (record < Block.RowCount)
        {
            ref readonly var row = ref Block.Rows[(int)record];
            if (row.IsInUse)
            {
                facts = new RecordFacts(row.IsDirectory, row.IsDeleted, Block.SequenceNumbers[(int)record]);
                return true;
            }
        }

        if (record == RootRecord && _rootRecord is { } root)
        {
            facts = root;
            return true;
        }

        facts = default;
        return false;
    }
}
