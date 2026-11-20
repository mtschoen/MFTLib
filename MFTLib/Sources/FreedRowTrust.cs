using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Decides which freed rows of a scan keep their parent. A freed record's parent column is only a hint: NTFS
///     reuses record numbers, so the directory it names may now be someone else. A freed row is trusted when every
///     hop of its chain reaches record 5 and each hop's parent is a directory whose stored sequence equals the
///     sequence the child's name referenced, or exactly one higher when the parent was freed too (NTFS bumps the
///     sequence when it frees a record, and 16-bit arithmetic wraps). The chain is at most
///     <see cref="BlockLayout.MaximumPathDepth" /> components, never revisits a record and rebuilds to a path of at
///     most <see cref="BlockLayout.MaximumPathUnits" /> UTF-16 units. A freed row that fails the rule is detached.
///     A row's verdict depends only on the chain above it, so each row's verdict is resolved once per pass and every
///     chain through it reuses it: the pass is linear in the rows it reaches.
/// </summary>
sealed class FreedRowTrust
{
    const uint RootRecord = 5;

    // A resolved verdict is either Untrusted or a chain that reaches the root, packed as ReachesRootFlag, its
    // UTF-16 name units shifted by DepthBits, and its component count. The limits keep both fields in range.
    const int Unresolved = 0;
    const int Untrusted = int.MinValue;
    const int CancellationInterval = 4096;
    const int DepthBits = 14;
    const int DepthMask = (1 << DepthBits) - 1;
    const int ReachesRootFlag = 1 << 30;

    readonly ushort[] _parentSequences;
    readonly List<uint> _freedRows = [];
    RecordFacts? _rootRecord;
    int[] _verdicts = [];

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
        if (_freedRows.Count == 0)
        {
            return;
        }

        _verdicts = new int[Block.RowCount];
        var untrusted = new List<uint>();
        foreach (var row in _freedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTrusted(row, cancellationToken))
            {
                untrusted.Add(row);
            }
        }

        _verdicts = [];
        foreach (var row in untrusted)
        {
            if (writer.DetachRow(row) != WriteRefusal.None)
            {
                writer.MarkCapacityExhausted();
            }

        }
    }

    // A freed root record is no root: nothing above it verifies it.
    bool IsTrusted(uint origin, CancellationToken cancellationToken) =>
        origin != RootRecord && Resolve(origin, cancellationToken) != Untrusted;

    // Climbs from the origin to the root, a resolved row or a failure, then hands the verdict back down the rows it
    // climbed. A pending row's verdict slot holds the bitwise complement of its descendant's row, with the origin
    // linking to itself. Meeting any pending row therefore closes a cycle, which never reaches the root.
    int Resolve(uint origin, CancellationToken cancellationToken)
    {
        var rowCount = Block.RowCount;
        var current = origin;
        var previous = origin;
        var pendingCount = 0;
        int verdict;
        while (true)
        {
            if (pendingCount % CancellationInterval == 0)
            {
                Block.Hooks._freedRowCheckpoint?.Invoke((Unwinding: false, Hops: pendingCount));
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (current == RootRecord)
            {
                verdict = ReachesRootFlag;
                break;
            }

            if (current >= rowCount)
            {
                verdict = Untrusted;
                break;
            }

            var known = _verdicts[current];
            if (known != Unresolved)
            {
                verdict = known < 0 ? Untrusted : known;
                break;
            }

            var nameLength = NamePool.ReadRowName(Block, current).Length;
            var parent = Block.Rows[(int)current].ParentRow;
            if (nameLength == 0 || !TrustsParentHop(current, parent))
            {
                _verdicts[current] = Untrusted;
                verdict = Untrusted;
                break;
            }

            _verdicts[current] = ~(int)previous;
            previous = current;
            pendingCount++;
            current = parent;
        }

        current = previous;
        for (var index = 0; index < pendingCount; index++)
        {
            if (index % CancellationInterval == 0)
            {
                Block.Hooks._freedRowCheckpoint?.Invoke((Unwinding: true, Hops: index));
                cancellationToken.ThrowIfCancellationRequested();
            }

            var next = (uint)~_verdicts[current];
            var nameLength = NamePool.ReadRowName(Block, current).Length;
            verdict = Extend(verdict, nameLength);
            _verdicts[current] = verdict;
            current = next;
        }

        return verdict;
    }

    // The verdict of a row whose own name has nameLength units under a parent with the given verdict. A chain past
    // either limit stays past it for every row below, so it is untrusted for all of them.
    static int Extend(int parentVerdict, int nameLength)
    {
        if (parentVerdict == Untrusted)
        {
            return Untrusted;
        }

        var depth = (parentVerdict & DepthMask) + 1;
        var units = (long)((parentVerdict & ~ReachesRootFlag) >> DepthBits) + nameLength;
        // The separators between the components are part of the path.
        if (depth > BlockLayout.MaximumPathDepth || units + depth - 1 > BlockLayout.MaximumPathUnits)
        {
            return Untrusted;
        }

        return ReachesRootFlag | ((int)units << DepthBits) | depth;
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
