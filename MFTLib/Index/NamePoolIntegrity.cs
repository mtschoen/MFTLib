namespace MFTLib.Index;

/// <summary>
///     The full integrity pass over a block's name regions. Production never calls it: open is constant-cost and
///     reads validate lazily. Tests reach it through <c>SyntheticNamePool.CheckIntegrity</c>.
/// </summary>
static class NamePoolIntegrity
{
    /// <summary>Returns null when every name region invariant holds, otherwise the first failure.</summary>
    internal static string? Check(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var entries = block.NameEntries;
        var buckets = block.Buckets;
        var count = block.NameEntryCount;
        for (uint nameId = 1; nameId <= count; nameId++)
        {
            var failure = NamePool.CheckEntry(block, entries[(int)nameId]);
            if (failure is not null)
            {
                return $"entry {nameId}: {failure}";
            }
        }

        var visited = 0u;
        for (var bucket = 0; bucket < buckets.Length; bucket++)
        {
            var current = buckets[bucket];
            while (current != 0)
            {
                if (current > count)
                {
                    return $"bucket {bucket} links to entry {current} past the published count";
                }

                var entry = entries[(int)current];
                if (entry.Next >= current)
                {
                    return $"entry {current} links to {entry.Next}, which is not smaller";
                }

                var name = NamePool.Read(block, current);
                if (!name.IsUtf16 && !System.Text.Ascii.IsValid(name.AsciiBytes))
                {
                    return $"entry {current} is flagged ASCII but holds a non-ASCII byte";
                }

                var hash = NameHash.Compute(name);
                if ((hash & (block.BucketCount - 1)) != (uint)bucket || entry.HashTag != (ushort)(hash >> 48))
                {
                    return $"entry {current} sits in the wrong bucket or carries the wrong tag";
                }

                visited++;
                current = entry.Next;
            }
        }

        // A decreasing chain cannot revisit an entry and its hash selects one bucket, so the total also proves
        // that no published entry is unreachable.
        if (visited != count)
        {
            return $"{count - visited} published entries are unreachable from the buckets";
        }

        var rows = block.Rows;
        for (var rowIndex = 0; rowIndex < block.RowCount; rowIndex++)
        {
            var descriptor = FileRow.ReadDescriptorWord(in rows[rowIndex]);
            if (FileRow.DescriptorNameId(descriptor) > count || FileRow.DescriptorReservedBits(descriptor) != 0)
            {
                return $"row {rowIndex} has an out-of-range name identifier or nonzero reserved bits";
            }
        }

        return null;
    }
}
