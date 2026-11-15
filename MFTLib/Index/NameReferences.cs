using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>
///     Which stored names of one block its rows reference: one bit per name identifier, held in native memory so a
///     growth of a large block puts nothing proportional to it on the managed heap. Counted in one pass over the
///     descriptor words of every row, whatever the row's flags, because a tombstoned or freed row keeps its name. A
///     name no row references is unused, and a compacting copy (<see cref="BlockCopier.CopyCompacting" />) drops it.
///     The block must not change while this is used: a watch growth counts and copies under the write gate.
/// </summary>
sealed unsafe class NameReferences : IDisposable
{
    readonly uint _rowCount;
    ulong* _bits;

    NameReferences(BlockFile block, uint rowCount, ulong* bits, NameUsage usage)
    {
        Block = block;
        _rowCount = rowCount;
        _bits = bits;
        Usage = usage;
    }

    ~NameReferences() => Free();

    /// <summary>The block the count was taken of: the source a growth plans and copies from.</summary>
    internal BlockFile Block { get; }

    /// <summary>
    ///     What compaction would keep, measured when the count was taken; it describes the block only while
    ///     <see cref="EnsureDescribes" /> accepts it.
    /// </summary>
    internal NameUsage Usage { get; }

    /// <summary>
    ///     Marks every name identifier the rows of <paramref name="block" /> reference, then measures the pool bytes those
    ///     names use when stored back to back in ascending identifier order.
    /// </summary>
    /// <exception cref="InvalidDataException">A row names an entry past the published name count, or a referenced entry is corrupt.</exception>
    /// <exception cref="ObjectDisposedException">The block has been disposed.</exception>
    internal static NameReferences Count(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        using var access = block.TakeAccess();
        var storedEntries = block.NameEntryCount;
        var rowCount = block.RowCount;
        var bits = (ulong*)NativeMemory.AllocZeroed(WordCount(storedEntries), sizeof(ulong));
        try
        {
            var referenced = Mark(block, rowCount, storedEntries, bits);
            var usage = new NameUsage(storedEntries, referenced, block.NamePoolUsed,
                MeasureCompactedPool(block, storedEntries, bits));
            return new NameReferences(block, rowCount, bits, usage);
        }
        catch
        {
            NativeMemory.Free(bits);
            throw;
        }
    }

    /// <summary>True when some row references <paramref name="nameId" />; identifier zero, no name, is never referenced.</summary>
    internal bool IsReferenced(uint nameId)
    {
        ObjectDisposedException.ThrowIf(_bits is null, this);
        return nameId != 0 && nameId <= Usage.StoredEntries && IsMarked(_bits, nameId);
    }

    /// <summary>
    ///     Checks that <paramref name="block" /> is the block this count was taken of and still has the same rows and
    ///     names, so the marks describe it.
    /// </summary>
    /// <exception cref="ArgumentException">The count describes a different block or an earlier state of it.</exception>
    internal void EnsureDescribes(BlockFile block)
    {
        if (!ReferenceEquals(block, Block) || block.RowCount != _rowCount ||
            block.NameEntryCount != Usage.StoredEntries || block.NamePoolUsed != Usage.StoredPoolBytes)
        {
            throw new ArgumentException("The name reference count describes a different block or an earlier state of it.",
                nameof(block));
        }
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    void Free()
    {
        var bits = _bits;
        _bits = null;
        NativeMemory.Free(bits);
    }

    static nuint WordCount(uint storedEntries) => storedEntries / 64 + 1;

    static bool IsMarked(ulong* bits, uint nameId) => (bits[nameId / 64] & (1UL << (int)(nameId % 64))) != 0;

    static uint Mark(BlockFile block, uint rowCount, uint storedEntries, ulong* bits)
    {
        var rows = block.Rows[..(int)rowCount];
        var referenced = 0u;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var nameId = FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in rows[rowIndex]));
            if (nameId == 0)
            {
                continue;
            }

            if (nameId > storedEntries)
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Row {rowIndex} of block file '{block.Path}' names entry {nameId}, past the {storedEntries} published names."));
            }

            ref var word = ref bits[nameId / 64];
            var mask = 1UL << (int)(nameId % 64);
            if ((word & mask) == 0)
            {
                word |= mask;
                referenced++;
            }
        }

        return referenced;
    }

    static uint MeasureCompactedPool(BlockFile block, uint storedEntries, ulong* bits)
    {
        var entries = block.NameEntries;
        var used = 0UL;
        for (var nameId = 1u; nameId <= storedEntries; nameId++)
        {
            if (IsMarked(bits, nameId))
            {
                used = NameCompaction.PlaceEntry(block, nameId, entries[(int)nameId], used, out _);
            }
        }

        return (uint)used;
    }
}
