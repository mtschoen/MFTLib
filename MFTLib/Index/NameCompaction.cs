using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>
///     The name phase of a compacting copy (<see cref="BlockCopier.CopyCompacting" />): stores only the names some row
///     references into an empty target, in ascending old-identifier order, so the new identifiers keep the old order and
///     every chain still steps to a smaller identifier. An old-to-new identifier map held in native memory rewrites
///     each copied row's descriptor word; the chains are linked as the names are stored, for the target's bucket count.
/// </summary>
static unsafe class NameCompaction
{
    /// <summary>
    ///     Copies the referenced names of <paramref name="source" /> into <paramref name="target" />, publishes the
    ///     compacted counts, and rewrites the name identifier of the first <paramref name="rowCount" /> target rows,
    ///     which <see cref="BlockCopier.CopyRows" /> already copied.
    /// </summary>
    /// <exception cref="InvalidDataException">A referenced entry or a copied row's identifier is corrupt.</exception>
    internal static void CopyNames(BlockFile source, BlockFile target, NameReferences references, uint rowCount)
    {
        references.EnsureDescribes(source);
        var storedEntries = source.NameEntryCount;
        var map = (uint*)NativeMemory.Alloc((nuint)storedEntries + 1, sizeof(uint));
        try
        {
            map[0] = 0;
            var used = StoreReferencedNames(source, target, references, map);
            target.PublishNamePoolUsed(used);
            target.PublishNameEntryCount(references.Usage.ReferencedEntries);
            RewriteNameIds(target, rowCount, storedEntries, map);
        }
        finally
        {
            NativeMemory.Free(map);
        }
    }

    /// <summary>
    ///     Where a compacting copy stores the entry <paramref name="nameId" /> when the target pool has
    ///     <paramref name="used" /> bytes: at that offset, or the next even one for a UTF-16 name.
    /// </summary>
    /// <returns>The used pool bytes after storing it.</returns>
    /// <exception cref="InvalidDataException">The entry fails the read-safety checks of the source block.</exception>
    internal static ulong PlaceEntry(BlockFile source, uint nameId, NameEntry entry, ulong used, out uint offset)
    {
        if (NamePool.CheckEntry(source, entry) is { } failure)
        {
            throw new InvalidDataException(FormattableString.Invariant(
                $"Block file '{source.Path}' has a corrupt name entry {nameId}: {failure}."));
        }

        var start = entry.IsUtf16 ? (used + 1) & ~1UL : used;
        offset = (uint)start;
        return start + ByteCount(entry);
    }

    static uint ByteCount(NameEntry entry) => (uint)entry.Length * (entry.IsUtf16 ? sizeof(char) : 1u);

    static uint StoreReferencedNames(BlockFile source, BlockFile target, NameReferences references, uint* map)
    {
        var sourceEntries = source.NameEntries;
        var targetEntries = target.NameEntries;
        var buckets = target.Buckets;
        var bucketMask = target.BucketCount - 1;
        var used = 0UL;
        var nextId = 0u;
        for (var oldId = 1u; oldId <= references.Usage.StoredEntries; oldId++)
        {
            if (!references.IsReferenced(oldId))
            {
                // No row holds it, so the rewrite never looks it up.
                map[oldId] = 0;
                continue;
            }

            var entry = sourceEntries[(int)oldId];
            used = PlaceEntry(source, oldId, entry, used, out var offset);
            var byteCount = (int)ByteCount(entry);
            source.GetNamePoolBytes(entry.PoolOffsetBytes, byteCount).CopyTo(target.GetNamePoolBytes(offset, byteCount));
            var bucket = (int)(NameHash.Compute(NamePool.Read(source, oldId)) & bucketMask);
            nextId++;
            targetEntries[(int)nextId] = entry with { PoolOffsetBytes = offset, Next = buckets[bucket] };
            buckets[bucket] = nextId;
            map[oldId] = nextId;
        }

        return (uint)used;
    }

    static void RewriteNameIds(BlockFile target, uint rowCount, uint storedEntries, uint* map)
    {
        var rows = target.Rows[..(int)rowCount];
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            ref var row = ref rows[rowIndex];
            var descriptor = FileRow.ReadDescriptorWord(in row);
            var nameId = FileRow.DescriptorNameId(descriptor);
            if (nameId == 0)
            {
                continue;
            }

            if (nameId > storedEntries)
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Row {rowIndex} of block file '{target.Path}' names entry {nameId}, past the {storedEntries} copied names."));
            }

            FileRow.WriteDescriptorWord(ref row, map[nameId], FileRow.DescriptorFlags(descriptor));
        }
    }
}
