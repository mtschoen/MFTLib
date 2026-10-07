namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>
    ///     Opens the underlying file. Enumeration entries open by path. For MFT entries,
    ///     the route is the NTFS file id opened against a handle on the target volume,
    ///     which needs no elevation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's snapshot released.</exception>
    /// <exception cref="InvalidOperationException">The entry belongs to an MFT dump, whose files exist only as index rows.</exception>
    public FileStream Open(FileAccess access)
    {
        var driveBlock = DriveBlock;
        if (driveBlock.IsMftDump)
        {
            throw new InvalidOperationException("Files in an MFT dump cannot be opened through the index.");
        }

        if (driveBlock.ProducerKind == ProducerKind.Enumeration)
        {
            return new FileStream(Path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
        }

        if (driveBlock.RootDirectoryPath is not { } anyPathOnVolume)
        {
            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} has no configured root directory to open by file id.");
        }

        return _openById(anyPathOnVolume, RowIndex, driveBlock.Block.SequenceNumbers[(int)RowIndex], access);
    }

    static FileStream DefaultOpenById(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber,
        FileAccess access) =>
        OpenById(OperatingSystem.IsWindows(), anyPathOnVolume, recordNumber, sequenceNumber, access);

    /// <summary>
    ///     The route behind <see cref="DefaultOpenById" />, given whether the host is Windows, so the
    ///     refusal off Windows is tested on every platform.
    /// </summary>
    internal static FileStream OpenById(bool isWindows, string anyPathOnVolume, uint recordNumber,
        ushort sequenceNumber, FileAccess access)
    {
        if (!isWindows || !OperatingSystem.IsWindows())
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
}
