using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Calculates slot and name pool capacities for packed index blocks from NTFS volume information. The pool
///     reservation is the layout's one routine, shared with the enumeration producer.
/// </summary>
static class MftBlockCapacity
{
    /// <summary>
    ///     Minimum row count floor used when volume information is unqueried.
    /// </summary>
    public const uint MinimumEstimatedRowCount = 65536;

    /// <summary>
    ///     Ceiling for an estimated row count, leaving room for the slot headroom that
    ///     <see cref="BlockLayout.ComputeSlotCapacity" /> adds on top of it.
    /// </summary>
    const uint MaximumEstimatedRowCount = uint.MaxValue / 2;

    /// <summary>
    ///     Estimates the row count from NTFS volume information, falling back to
    ///     <see cref="MinimumEstimatedRowCount" /> when volume information is degenerate.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information.</param>
    /// <returns>
    ///     The estimated row count, clamped low enough that the quarter headroom
    ///     <see cref="BlockLayout.ComputeSlotCapacity" /> adds still fits in 32 bits.
    /// </returns>
    public static uint EstimateRowCount(NtfsVolumeInformation volumeInformation)
    {
        var recordCount = volumeInformation.MftRecordCount;
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
    ///     The creation options of the block a cold MFT scan of one volume fills: planned from the
    ///     volume geometry, rooted at MFT record 5, in the file, mode and tag the request names.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information that sizes the block.</param>
    /// <param name="path">Where the block file is created.</param>
    /// <param name="volume">The volume stored in the block as its root key.</param>
    /// <param name="volumeSerial">The volume serial number to stamp into the header.</param>
    /// <param name="deleteOnClose">Whether the operating system deletes the file when its last handle closes.</param>
    /// <param name="identity">The consumer cache tag and scan shape copied into the header.</param>
    /// <returns>Options for <see cref="BlockFile.Create" />.</returns>
    public static BlockFileCreateOptions CreateOptions(NtfsVolumeInformation volumeInformation, VolumeKey volume,
        string path, uint volumeSerial, bool deleteOnClose, BlockContentIdentity identity)
    {
        var (slotCapacity, namePoolCapacity) = Plan(volumeInformation);
        return new BlockFileCreateOptions
        {
            Path = path,
            Root = volume,
            VolumeSerial = volumeSerial,
            DeleteOnClose = deleteOnClose,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity,
            CacheTag = identity.CacheTag,
            ScanShape = identity.ScanShape
        };
    }

    /// <summary>
    ///     Computes slot capacity and name pool capacity for a packed index block with headroom.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information.</param>
    /// <returns>A tuple containing the planned slot capacity and name pool capacity.</returns>
    public static (uint SlotCapacity, uint NamePoolCapacity) Plan(
        NtfsVolumeInformation volumeInformation)
    {
        var slotCapacity = BlockLayout.ComputeSlotCapacity(EstimateRowCount(volumeInformation));
        return (slotCapacity, BlockLayout.ComputeNamePoolCapacity(slotCapacity));
    }
}
