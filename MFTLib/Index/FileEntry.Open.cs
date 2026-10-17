using MFTLib.NativeInterop;

namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    internal static readonly OpenDependencies Dependencies = new();

    /// <summary>Process-global test dependencies for opening MFT entries by file id.</summary>
    internal sealed class OpenDependencies
    {
        // aislop-ignore-next-line jb/MemberHidesStaticFromOuterClass
        internal Func<string, uint, ushort, FileAccess, FileStream> OpenById = DefaultOpenById;

        internal void ResetToDefaults() => OpenById = DefaultOpenById;
    }

    /// <summary>
    ///     Opens the underlying file. Enumeration entries open by path. For MFT entries,
    ///     the route is the NTFS file id opened against a handle on the target volume,
    ///     which needs no elevation, with the sequence number this handle was minted with, so it can
    ///     only ever open the file it names.
    /// </summary>
    /// <exception cref="FileNotFoundException">A newer file has reused this entry's row slot; nothing is opened. The refusal message includes this entry's <see cref="ToString" /> text.</exception>
    /// <exception cref="ObjectDisposedException">The owning index has released this handle's drive block. The message includes this entry's <see cref="ToString" /> text.</exception>
    /// <exception cref="InvalidOperationException">The entry is default, belongs to an MFT dump, or its MFT drive has no configured root directory. The refusal message includes this entry's <see cref="ToString" /> text.</exception>
    public FileStream Open(FileAccess access)
    {
        var driveBlock = DriveBlock;
        if (IsSupersededIn(driveBlock))
        {
            throw new FileNotFoundException($"{this}: The entry has been deleted.");
        }

        if (!driveBlock.Kind.CanOpen)
        {
            throw new InvalidOperationException($"{this}: Files in an MFT dump cannot be opened through the index.");
        }

        if (driveBlock.ProducerKind == ProducerKind.Enumeration)
        {
            return new FileStream(Path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
        }

        if (driveBlock.RootDirectoryPath is not { } anyPathOnVolume)
        {
            throw new InvalidOperationException(
                $"{this}: Drive block {driveBlock.Drive} has no configured root directory to open by file id.");
        }

        return Dependencies.OpenById(anyPathOnVolume, RowIndex, _sequenceNumber, access);
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
}
