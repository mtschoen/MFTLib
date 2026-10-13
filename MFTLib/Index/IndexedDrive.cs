namespace MFTLib.Index;

/// <summary>
///     One drive to index. <paramref name="RootDirectory" /> is where an enumeration walk
///     starts, and on a platform without drive letters the caller assigns
///     <paramref name="Drive" /> as a display and lookup key. The volume serial is part of
///     the block's file name so a re-lettered drive never matches the wrong block.
///     <paramref name="ProducerPolicy" /> selects how this drive's block is built on scans and rescans.
/// </summary>
public sealed partial record IndexedDrive(DriveKey Drive, string RootDirectory, uint VolumeSerial,
    ProducerPolicy ProducerPolicy = ProducerPolicy.Mft)
{
    /// <summary>Selects the index's drive identity; assigning the default key throws ArgumentException.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); } = Drive.Validated();
}
