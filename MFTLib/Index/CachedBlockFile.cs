namespace MFTLib.Index;

/// <summary>
///     Directory-derived metadata for one block file found in a cache directory, recognised by its
///     name and directory entry, plus the root key text stored in page 0 of a directory-shaped file.
///     Nothing else was read from inside the block;
///     <see cref="CacheDirectory.InspectCached" />
///     separately opens and validates available blocks under their slot locks.
/// </summary>
public sealed record CachedBlockFile
{
    /// <summary>Creates an inventory entry.</summary>
    /// <param name="root">The root: from the file name for a volume, from the stored key for a directory.</param>
    /// <param name="volumeSerial">The volume serial from the file name.</param>
    /// <param name="path">The full path to the cached file.</param>
    /// <param name="sizeBytes">The file size in bytes.</param>
    /// <param name="lastWriteTimeUtc">The file's last-write time in UTC.</param>
    internal CachedBlockFile(RootKey root, uint volumeSerial, string path, long sizeBytes,
        DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        Root = root;
        VolumeSerial = volumeSerial;
        Path = path;
        Size = sizeBytes;
        LastWriteTime = lastWriteTimeUtc;
    }

    /// <summary>The root this block belongs to: the volume letter from the file name, or the key stored in the block for a directory root.</summary>
    public RootKey Root { get; init => field = value.Validated(); }

    /// <summary>Serial of the volume containing the root, distinguishing volumes mounted at one letter over time; parsed from the file name.</summary>
    public uint VolumeSerial { get; init; }

    /// <summary>Where the block file lives.</summary>
    public string Path { get; init; }

    /// <summary>The on-disk length at enumeration time, which a concurrent writer may since have changed.</summary>
    public long Size { get; init; }

    /// <summary>The file's last-write time, always in UTC.</summary>
    public DateTime LastWriteTime { get; init; }
}
