using MFTLib.NativeInterop;

namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    internal static readonly OpenDependencies Dependencies = new();

    /// <summary>Process-global test dependencies for opening MFT entries by file id.</summary>
    internal sealed class OpenDependencies
    {
        internal Func<string, uint, ushort, ulong, FileAccess, FileStream> _openById = DefaultOpenById;

        internal void ResetToDefaults() => _openById = DefaultOpenById;
    }

    /// <summary>
    ///     Opens the underlying file. Enumeration entries open by path. For MFT entries,
    ///     the route is the NTFS file id opened against a handle on the target volume,
    ///     which needs no elevation, with the sequence number this handle was minted with, so it can
    ///     only ever open the file it names. The opened handle's full 64-bit volume serial must equal the
    ///     serial the root block records. A different or unreadable serial is refused; a clone that retains
    ///     the serial and file reference is outside this check.
    /// </summary>
    /// <exception cref="FileNotFoundException">A newer file has reused this entry's row slot; nothing is opened. The refusal message includes this entry's <see cref="ToString" /> text.</exception>
    /// <exception cref="IOException">The opened handle's volume serial differs from the serial the block records, the serial cannot be read, or the block records none; the handle is closed and nothing is returned.</exception>
    /// <exception cref="ObjectDisposedException">The owning index has released this handle's root block. The message includes this entry's <see cref="ToString" /> text.</exception>
    /// <exception cref="InvalidOperationException">The entry is default, belongs to an MFT dump, or its MFT volume has no configured root directory. The refusal message includes this entry's <see cref="ToString" /> text.</exception>
    public FileStream Open(FileAccess access)
    {
        var rootBlock = RootBlock;
        var pinned = PinnedRootBlock.Current(rootBlock);
        if (IsSupersededIn(pinned))
        {
            throw new FileNotFoundException($"{this}: The entry has been deleted.");
        }

        if (!rootBlock.Kind.CanOpen)
        {
            throw new InvalidOperationException($"{this}: Files in an MFT dump cannot be opened through the index.");
        }

        if (rootBlock.ProducerKind == ProducerKind.Enumeration)
        {
            return new FileStream(Path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
        }

        if (rootBlock.RootDirectoryPath is not { } anyPathOnVolume)
        {
            throw new InvalidOperationException(
                $"{this}: Root block {rootBlock.Root} has no configured root directory to open by file id.");
        }

        return Dependencies._openById(anyPathOnVolume, RowIndex, _sequenceNumber, pinned.Block.Header.VolumeSerial, access);
    }

    static FileStream DefaultOpenById(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber,
        ulong expectedVolumeSerial, FileAccess access) =>
        OpenById(OperatingSystem.IsWindows(), anyPathOnVolume, recordNumber, sequenceNumber, expectedVolumeSerial,
            access);

    /// <summary>
    ///     The route behind <see cref="DefaultOpenById" />, given whether the host is Windows, so the
    ///     refusal off Windows is tested on every platform.
    /// </summary>
    internal static FileStream OpenById(bool isWindows, string anyPathOnVolume, uint recordNumber,
        ushort sequenceNumber, ulong expectedVolumeSerial, FileAccess access)
    {
        if (!isWindows || !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Opening an MFT entry by file id is only supported on Windows.");
        }

        return WindowsFileById.Open(anyPathOnVolume, recordNumber, sequenceNumber, expectedVolumeSerial, access);
    }
}
