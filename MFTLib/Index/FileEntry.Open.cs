namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>
    ///     Opens the underlying file. Enumeration entries open by path. MFT entries open through
    ///     the NTFS file id against a handle on the target volume, which needs no elevation; the
    ///     route is here, in <see cref="DefaultOpenById" /> below.
    /// </summary>
    public FileStream Open(FileAccess access)
    {
        var driveBlock = DriveBlock;
        if (driveBlock.ProducerKind == ProducerKind.Enumeration)
        {
            return new FileStream(ResolveRealPath(driveBlock), FileMode.Open, access,
                FileShare.ReadWrite | FileShare.Delete);
        }

        if (driveBlock.RootDirectoryPath is not { } anyPathOnVolume)
        {
            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} has no configured root directory to open by file id.");
        }

        return _openById(anyPathOnVolume, RowIndex, driveBlock.Block.SequenceNumbers[(int)RowIndex], access);
    }

    static FileStream DefaultOpenById(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber,
        FileAccess access)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Opening an MFT entry by file id is only supported on Windows.");
        }

        return WindowsFileById.Open(anyPathOnVolume, recordNumber, sequenceNumber, access);
    }

    /// <summary>
    ///     A test seam with no synchronization, which is safe only while the test host runs one
    ///     test at a time. <c>IndexedDrive._volumeSerialReaderOverride</c> is the same seam in the
    ///     same shape, so enabling test parallelism has to answer for both together.
    /// </summary>
    static Func<string, uint, ushort, FileAccess, FileStream> _openById = DefaultOpenById;

    /// <summary>
    ///     Replaces the by-id open seam for the duration of a test. Restores the previous
    ///     delegate on <see cref="IDisposable.Dispose" />, the same shape as
    ///     <c>IndexedDrive.OverrideVolumeSerialReaderForTest</c>.
    /// </summary>
    internal static IDisposable OverrideOpenByIdForTest(
        Func<string, uint, ushort, FileAccess, FileStream> replacement)
    {
        var previous = _openById;
        _openById = replacement;
        return new RestoreOpenById(previous);
    }

    sealed class RestoreOpenById(Func<string, uint, ushort, FileAccess, FileStream> previous) : IDisposable
    {
        public void Dispose()
        {
            _openById = previous;
        }
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
