namespace MFTLib.Index;

/// <summary>
///     One drive to index. <paramref name="RootDirectory" /> is where an enumeration walk
///     starts, and on a platform without drive letters the caller assigns
///     <paramref name="DriveLetter" /> as a display and lookup key. The volume serial is part of
///     the block's file name so a re-lettered drive never matches the wrong block.
/// </summary>
public sealed record IndexedDrive(char DriveLetter, string RootDirectory, uint VolumeSerial);
