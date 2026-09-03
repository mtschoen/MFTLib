namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>
    ///     Opens the underlying file. Enumeration entries open by path. MFT entries open through
    ///     the NTFS file id against the volume root, which needs no elevation, and that route
    ///     arrives with the MFT producer.
    /// </summary>
    public FileStream Open(FileAccess access)
    {
        var driveBlock = DriveBlock;
        if (driveBlock.ProducerKind != ProducerKind.Enumeration)
        {
            throw new NotSupportedException(
                "Opening an MFT-producer entry by file id is not available in this build.");
        }

        return new FileStream(ResolveRealPath(driveBlock), FileMode.Open, access,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    ///     An enumeration entry's <see cref="Path" /> is a logical path rooted at the configured
    ///     drive letter, which need not be a real filesystem root: a caller can index any
    ///     directory under any letter, and on a platform with no drive letters at all the letter
    ///     is only a display and lookup key. This rebuilds the real path the producer actually
    ///     read from, by joining the drive's configured root directory
    ///     (<see cref="DriveBlock.RootDirectoryPath" />) with every segment of the logical path
    ///     after its three-character drive-letter prefix, one path component at a time so no
    ///     platform-specific separator assumption leaks in.
    /// </summary>
    string ResolveRealPath(DriveBlock driveBlock)
    {
        if (driveBlock.RootDirectoryPath is not { } rootDirectoryPath)
        {
            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} has no configured root directory to open by path.");
        }

        var relativeSegments = Path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var components = new string[relativeSegments.Length + 1];
        components[0] = rootDirectoryPath;
        relativeSegments.CopyTo(components, 1);
        return System.IO.Path.Combine(components);
    }
}
