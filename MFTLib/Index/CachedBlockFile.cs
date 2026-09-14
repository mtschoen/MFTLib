namespace MFTLib.Index;

/// <summary>
///     One block file found in a cache directory, recognised by its name alone. Nothing here was
///     read from inside the block: <see cref="CacheDirectory.EnumerateCached" /> does not open or
///     validate blocks, so a file listed here may still be rejected when the index opens it. The
///     drive's root directory is not part of this record because it lives inside the block.
/// </summary>
/// <param name="DriveLetter">The uppercase ASCII drive letter from the file name.</param>
/// <param name="VolumeSerial">The volume serial from the file name.</param>
/// <param name="Path">The full path to the cached file.</param>
/// <param name="SizeBytes">The file size in bytes.</param>
/// <param name="LastWriteTimeUtc">The file's last-write time in UTC.</param>
public sealed record CachedBlockFile(
    char DriveLetter,
    uint VolumeSerial,
    string Path,
    long SizeBytes,
    DateTime LastWriteTimeUtc);
