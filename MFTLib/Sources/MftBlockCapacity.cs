using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Calculates slot, name entry and name pool capacities for packed index blocks from NTFS volume information.
///     The layout's one planner is shared with the enumeration producer.
/// </summary>
static class MftBlockCapacity
{
    internal readonly record struct FileLocation(VolumeKey Volume, string Path, uint VolumeSerial, bool DeleteOnClose);

    /// <summary>
    ///     Minimum row count floor used when volume information is unqueried.
    /// </summary>
    public const uint MinimumEstimatedRowCount = 65536;

    /// <summary>
    ///     Estimates the row count from NTFS volume information, falling back to
    ///     <see cref="MinimumEstimatedRowCount" /> when volume information is degenerate.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information.</param>
    /// <returns>The estimated row count, clamped to the largest estimate with a creatable slot capacity.</returns>
    public static uint EstimateRowCount(NtfsVolumeInformation volumeInformation)
    {
        var recordCount = volumeInformation.MftRecordCount;
        if (recordCount <= MinimumEstimatedRowCount)
        {
            return MinimumEstimatedRowCount;
        }

        return recordCount > BlockLayout.MaximumEstimatedRowCount
            ? BlockLayout.MaximumEstimatedRowCount
            : (uint)recordCount;
    }

    /// <summary>
    ///     The creation options of the block a cold MFT scan of one volume fills: planned from the
    ///     volume geometry, rooted at MFT record 5, in the file, mode and tag the request names.
    /// </summary>
    /// <param name="volumeInformation">The NTFS volume information that sizes the block.</param>
    /// <param name="location">The volume, file, serial and delete-on-close choice of the block.</param>
    /// <param name="identity">The consumer cache tag and scan shape copied into the header.</param>
    /// <param name="minimum">Regional minima the replacement block must meet.</param>
    /// <returns>Options for <see cref="BlockFile.Create" />.</returns>
    public static BlockFileCreateOptions CreateOptions(NtfsVolumeInformation volumeInformation, FileLocation location,
        BlockContentIdentity identity, BlockCapacities minimum)
    {
        var capacities = Plan(volumeInformation, minimum);
        return new BlockFileCreateOptions
        {
            Path = location.Path,
            Root = location.Volume,
            VolumeSerial = location.VolumeSerial,
            DeleteOnClose = location.DeleteOnClose,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = capacities.SlotCapacity,
            NameEntryCapacity = capacities.NameEntryCapacity,
            NamePoolCapacity = capacities.NamePoolCapacity,
            CacheTag = identity.CacheTag,
            ScanShape = identity.ScanShape
        };
    }

    /// <summary>Computes every region's capacity from the volume geometry and independent regional minima.</summary>
    /// <param name="volumeInformation">The NTFS volume information.</param>
    /// <param name="minimum">Regional minima the planned block must meet.</param>
    /// <returns>The three planned capacities.</returns>
    public static BlockCapacities Plan(NtfsVolumeInformation volumeInformation, BlockCapacities minimum)
    {
        return BlockLayout.PlanCapacities(EstimateRowCount(volumeInformation), minimum);
    }
}
