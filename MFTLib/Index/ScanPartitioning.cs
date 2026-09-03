namespace MFTLib.Index;

/// <summary>
///     Splits one drive block's row range into contiguous partitions for a parallel scan. Small
///     drives get a single partition: below the threshold, starting tasks costs more than the
///     scan itself.
/// </summary>
internal static class ScanPartitioning
{
    /// <summary>Below this many rows a scan stays on the calling thread.</summary>
    internal const uint SingleThreadedRowThreshold = 32768;

    internal static int DefaultPartitionCount(uint rowCount)
    {
        return rowCount < SingleThreadedRowThreshold ? 1 : Environment.ProcessorCount;
    }

    internal static List<(uint StartRow, uint EndRowExclusive)> Partition(uint rowCount, int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        var partitions = new List<(uint StartRow, uint EndRowExclusive)>(partitionCount);
        if (rowCount == 0)
        {
            return partitions;
        }

        var effectiveCount = Math.Min((uint)partitionCount, rowCount);
        var baseSize = rowCount / effectiveCount;
        var remainder = rowCount % effectiveCount;

        var start = 0u;
        for (var index = 0u; index < effectiveCount; index++)
        {
            var size = baseSize + (index < remainder ? 1u : 0u);
            partitions.Add((start, start + size));
            start += size;
        }

        return partitions;
    }
}
