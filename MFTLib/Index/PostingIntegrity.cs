namespace MFTLib.Index;

/// <summary>
///     The full integrity pass over a block's posting regions. Production never calls it: open is constant-cost and
///     reads validate lazily. Tests reach it through <c>SyntheticNamePool.CheckPostingIntegrity</c>.
/// </summary>
static class PostingIntegrity
{
    /// <summary>Returns null when every posting invariant holds, otherwise the first failure.</summary>
    internal static string? Check(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return CheckBase(block) ?? CheckDeltas(block) ?? CheckEveryNamedRowIsReachable(block);
    }

    static string? CheckBase(BlockFile block)
    {
        var baseNameCount = block.PostingBaseNameCount;
        var offsets = block.PostingOffsets;
        if (baseNameCount > block.NameEntryCount)
        {
            return $"the base covers {baseNameCount} names but only {block.NameEntryCount} are published";
        }

        if (offsets[0] != 0 || offsets[1] != 0)
        {
            return "the reserved offsets for name zero are not zero";
        }

        if (offsets[(int)baseNameCount + 1] != block.PostingBaseRowCount || block.PostingBaseRowCount > block.RowCount)
        {
            return $"the final offset {offsets[(int)baseNameCount + 1]} does not equal the base row count {block.PostingBaseRowCount}";
        }

        var postingRows = block.PostingRows;
        for (var nameId = 1; nameId <= baseNameCount; nameId++)
        {
            var start = offsets[nameId];
            var end = offsets[nameId + 1];
            if (start > end || end > block.PostingBaseRowCount)
            {
                return $"name {nameId} owns the invalid slot range [{start}, {end})";
            }

            for (var slot = start; slot < end; slot++)
            {
                if (postingRows[(int)slot] >= block.RowCount)
                {
                    return $"name {nameId} lists row {postingRows[(int)slot]}, past the row count";
                }

                if (slot > start && postingRows[(int)slot] <= postingRows[(int)slot - 1])
                {
                    return $"name {nameId} lists rows out of ascending order at slot {slot}";
                }
            }
        }

        return null;
    }

    static string? CheckDeltas(BlockFile block)
    {
        var count = block.DeltaEntryCount;
        var heads = block.DeltaHeads;
        var entries = block.DeltaEntries;
        var visited = 0u;
        for (var head = 0; head < heads.Length; head++)
        {
            for (var current = heads[head]; current != 0; current = entries[(int)current].Next)
            {
                if (current > count)
                {
                    return $"delta head {head} links to entry {current} past the published count";
                }

                var entry = entries[(int)current];
                if (entry.Next >= current)
                {
                    return $"delta entry {current} links to {entry.Next}, which is not smaller";
                }

                if (entry.NameId == 0 || entry.NameId > block.NameEntryCount || entry.Row >= block.RowCount)
                {
                    return $"delta entry {current} holds an out-of-range name or row";
                }

                if ((entry.NameId & (block.BucketCount - 1)) != (uint)head)
                {
                    return $"delta entry {current} sits in the wrong head";
                }

                visited++;
            }
        }

        return visited != count ? $"{count - visited} published delta entries are unreachable from the heads" : null;
    }

    static string? CheckEveryNamedRowIsReachable(BlockFile block)
    {
        var rows = block.Rows;
        var offsets = block.PostingOffsets;
        var postingRows = block.PostingRows;
        var baseNameCount = block.PostingBaseNameCount;
        var heads = block.DeltaHeads;
        var entries = block.DeltaEntries;
        for (var rowIndex = 0; rowIndex < block.RowCount; rowIndex++)
        {
            var nameId = FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in rows[rowIndex]));
            if (nameId == 0)
            {
                continue;
            }

            var inBase = nameId <= baseNameCount &&
                         postingRows.Slice((int)offsets[(int)nameId], (int)(offsets[(int)nameId + 1] - offsets[(int)nameId]))
                             .BinarySearch((uint)rowIndex) >= 0;
            if (!inBase && !InDelta(heads, entries, nameId, (uint)rowIndex))
            {
                return $"row {rowIndex} holds name {nameId} but neither the base nor the delta chain lists it";
            }
        }

        return null;
    }

    static bool InDelta(ReadOnlySpan<uint> heads, ReadOnlySpan<DeltaEntry> entries, uint nameId, uint rowIndex)
    {
        for (var current = heads[(int)(nameId & (uint)(heads.Length - 1))]; current != 0; current = entries[(int)current].Next)
        {
            if (entries[(int)current].NameId == nameId && entries[(int)current].Row == rowIndex)
            {
                return true;
            }
        }

        return false;
    }
}
