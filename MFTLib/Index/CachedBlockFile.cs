namespace MFTLib.Index;

/// <summary>
///     Directory-derived metadata for one block file found in a cache directory, recognised by its
///     name and directory entry alone. Nothing here was read from inside the block;
///     <see cref="CacheDirectory.InspectCached(string, System.Collections.Generic.IReadOnlySet{char}?)" />
///     separately opens and validates available blocks under their slot locks. The drive's root
///     directory is not part of this record because it lives inside the block.
/// </summary>
public sealed record CachedBlockFile
{
    /// <summary>Creates an inventory entry.</summary>
    /// <param name="driveLetter">The uppercase ASCII drive letter from the file name.</param>
    /// <param name="volumeSerial">The volume serial from the file name.</param>
    /// <param name="path">The full path to the cached file.</param>
    /// <param name="sizeBytes">The file size in bytes.</param>
    /// <param name="lastWriteTimeUtc">The file's last-write time in UTC.</param>
    internal CachedBlockFile(char driveLetter, uint volumeSerial, string path, long sizeBytes,
        DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        DriveLetter = driveLetter;
        VolumeSerial = volumeSerial;
        Path = path;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTimeUtc;
    }

    /// <summary>The drive this block belongs to, parsed from the file name as an uppercase ASCII letter.</summary>
    public char DriveLetter { get; init; }

    /// <summary>Distinguishes two volumes mounted at one letter over time; parsed from the file name.</summary>
    public uint VolumeSerial { get; init; }

    /// <summary>Where the block file lives; pass it to the cache-directory operations that take a block path.</summary>
    public string Path { get; init; }

    /// <summary>The on-disk length at enumeration time, which a concurrent writer may since have changed.</summary>
    public long SizeBytes { get; init; }

    /// <summary>The file's last-write time, always in UTC.</summary>
    public DateTime LastWriteTime { get; init; }
}
