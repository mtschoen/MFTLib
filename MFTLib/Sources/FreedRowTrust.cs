using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Decides which freed rows of a scan keep their parent. A freed record's parent column is only a hint: NTFS
///     reuses record numbers, so the directory it names may now be someone else. A freed row is trusted when every
///     hop of its chain reaches record 5 and each hop's parent is a directory whose stored sequence equals the
///     sequence the child's name referenced, or exactly one higher when the parent was freed too (NTFS bumps the
///     sequence when it frees a record, and 16-bit arithmetic wraps). The walk is at most
///     <see cref="BlockLayout.MaximumPathDepth" /> components, never revisits a record and rebuilds to a path of at
///     most <see cref="MaximumPathUnits" /> UTF-16 units. A freed row that fails the rule is detached.
///     The native freed resolver implements the same rule; the fixture parity test keeps the two equal.
/// </summary>
internal sealed class FreedRowTrust
{
    // The longest path NTFS can name, in UTF-16 code units.
    internal const int MaximumPathUnits = 32767;

    const uint RootRecord = 5;

    readonly BlockFile _block;
    readonly ushort[] _parentSequences;
    readonly List<uint> _freedRows = [];
    RecordFacts? _rootRecord;

    /// <summary>What the rule needs to know about a record that is a hop's parent.</summary>
    readonly record struct RecordFacts(bool IsDirectory, bool IsFreed, ushort Sequence);

    public FreedRowTrust(BlockFile block)
    {
        _block = block;
        _parentSequences = new ushort[block.Header.SlotCapacity];
    }

    /// <summary>
    ///     Remembers record 5 even when it gets no row, because the rule asks the root only for its kind and sequence,
    ///     never for a name.
    /// </summary>
    public void Observe(in MftRecord record)
    {
        if (record.RecordNumber == RootRecord)
        {
            _rootRecord = new RecordFacts(record.IsDirectory, !record.InUse, record.SequenceNumber);
        }
    }

    /// <summary>Records what the rule needs of a record that was written as a row.</summary>
    public void Wrote(in MftRecord record)
    {
        var row = (uint)record.RecordNumber;
        _parentSequences[row] = record.ParentSequenceNumber;
        if (!record.InUse)
        {
            _freedRows.Add(row);
        }
    }

    /// <summary>
    ///     Applies the rule to every freed row once the last batch is written, since a parent can arrive after its
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

        Span<uint> visited = stackalloc uint[BlockLayout.MaximumPathDepth];
        var visitedCount = 0;
        var depth = 0;
        long units = 0;
        var current = origin;
        var rowCount = _block.Header.RowCount;
        while (current != RootRecord && current < rowCount && depth < BlockLayout.MaximumPathDepth)
        {
            if (visited[..visitedCount].Contains(current))
            {
                break;
            }

            visited[visitedCount++] = current;
            var nameLength = NamePool.ReadRowName(_block, current).Length;
            if (nameLength == 0)
            {
                break;
            }

            units += nameLength;
            depth++;
            var parent = _block.Rows[(int)current].ParentRow;
            if (!TrustsParentHop(current, parent))
            {
                return false;
            }

            current = parent;
        }

        // The separators between the components are part of the path.
        return current == RootRecord && units + depth - 1 <= MaximumPathUnits;
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
        if (record < _block.Header.RowCount)
        {
            ref readonly var row = ref _block.Rows[(int)record];
            if (row.IsInUse)
            {
                facts = new RecordFacts(row.IsDirectory, row.IsDeleted, _block.SequenceNumbers[(int)record]);
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
