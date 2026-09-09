using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Calculates slot and name pool capacities for packed index blocks from NTFS volume information.
/// </summary>
public static class MftBlockCapacity
{
    /// <summary>
    ///     Default estimated bytes per name entry in the block name pool.
    /// </summary>
    public const uint DefaultAverageNameBytesPerRow = 48;

    /// <summary>
    ///     Minimum row count floor used when volume information is absent or unqueried.
    /// </summary>
    public const uint MinimumEstimatedRowCount = 65536;

    /// <summary>
    ///     Ceiling for an estimated row count, leaving room for the slot headroom that
    ///     <see cref="BlockLayout.ComputeSlotCapacity" /> adds on top of it.
    /// </summary>
    public const uint MaximumEstimatedRowCount = uint.MaxValue / 2;

    /// <summary>
    ///     Estimates the row count from NTFS volume information, falling back to
    ///     <see cref="MinimumEstimatedRowCount" /> when volume information is null or degenerate.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information, or null if unqueried.</param>
    /// <returns>
    ///     The estimated row count, clamped low enough that the quarter headroom
    ///     <see cref="BlockLayout.ComputeSlotCapacity" /> adds still fits in 32 bits.
    /// </returns>
    public static uint EstimateRowCount(NtfsVolumeInformation? volumeInformation)
    {
        var recordCount = volumeInformation?.MftRecordCount ?? 0;
        if (recordCount <= MinimumEstimatedRowCount)
        {
            return MinimumEstimatedRowCount;
        }

        // Clamped to half the range rather than the whole of it, mirroring the name-pool
        // clamp below: ComputeSlotCapacity adds a quarter under checked arithmetic, so
        // uint.MaxValue is the one estimate that throws instead of degrading gracefully.
        return recordCount > MaximumEstimatedRowCount ? MaximumEstimatedRowCount : (uint)recordCount;
    }

    /// <summary>
    ///     Computes slot capacity and name pool capacity for a packed index block with headroom.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information, or null if unqueried.</param>
    /// <param name="averageNameBytesPerRow">The expected average UTF-16 bytes per name entry.</param>
    /// <returns>A tuple containing the planned slot capacity and name pool capacity.</returns>
    public static (uint SlotCapacity, uint NamePoolCapacity) Plan(
        NtfsVolumeInformation? volumeInformation,
        uint averageNameBytesPerRow = DefaultAverageNameBytesPerRow)
    {
        ArgumentOutOfRangeException.ThrowIfZero(averageNameBytesPerRow);
        var slotCapacity = BlockLayout.ComputeSlotCapacity(EstimateRowCount(volumeInformation));

        // Widened before the multiply and clamped after: a large volume times a generous
        // average name length overflows 32 bits, and a saturated pool plus the block's own
        // compaction-needed flag is the right answer there, not a checked-arithmetic throw
        // on a path whose whole job is estimating.
        var estimatedNameBytes = (ulong)slotCapacity * averageNameBytesPerRow;
        var clamped = estimatedNameBytes > uint.MaxValue / 2 ? uint.MaxValue / 2 : (uint)estimatedNameBytes;
        return (slotCapacity, BlockLayout.ComputeNamePoolCapacity(clamped));
    }
}
