using MFTLib.NativeInterop;

namespace MFTLib.Index;

/// <summary>
///     What the library knows about the volume under a root before it picks a producer. Read once per open,
///     before the cache lookup. Reading a fact opens no volume handle and touches no <see cref="MftIndexSource" />.
/// </summary>
/// <param name="PlatformHasMft">Whether the host platform has an MFT to read at all.</param>
/// <param name="Volume">The drive letter of the volume, or null when it has none (a network share or an unlettered mount point).</param>
/// <param name="VolumeRootDirectory">The host directory the volume is rooted at, for example <c>C:\</c>; null with no letter.</param>
/// <param name="VolumeSerial">The full volume serial the file system reports, or zero when the platform reports none.</param>
/// <param name="IsReady">
///     Whether the volume read as ready. Readiness is read for fixed and removable drives only; every other drive type
///     reports true so that <paramref name="DriveType" /> decides.
/// </param>
/// <param name="DriveType">The operating system's classification of the volume.</param>
/// <param name="Format">The file system name, or null when it was not read.</param>
internal readonly record struct VolumeFacts(
    bool PlatformHasMft,
    VolumeKey? Volume,
    string? VolumeRootDirectory,
    ulong VolumeSerial,
    bool IsReady,
    DriveType DriveType,
    string? Format);

/// <summary>Reads the facts of the volume under one root; <paramref name="rootDirectory" /> is the root's host path.</summary>
internal delegate VolumeFacts VolumeFactsReader(RootKey root, string rootDirectory);

/// <summary>The production <see cref="VolumeFactsReader" />.</summary>
static class PlatformVolumeFacts
{
    /// <summary>
    ///     Reads the facts of the volume under <paramref name="rootDirectory" />. An I/O, access or security failure
    ///     from the platform is a volume that is not ready, never an exception.
    /// </summary>
    internal static VolumeFacts Read(RootKey root, string rootDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VolumeFacts(false, null, null, 0, false, DriveType.Unknown, null);
        }

        VolumeKey? fallbackVolume = root.TryGetVolume(out var rootVolume) ? rootVolume : null;
        try
        {
            var volumePath = HandleBoundFile.VolumePathOf(rootDirectory);
            var volume = LetterOf(volumePath);
            if (volume is null)
            {
                return new VolumeFacts(true, null, null, 0, true, DriveType.Unknown, null);
            }

            var drive = new DriveInfo(volumePath);
            var driveType = drive.DriveType;
            if (driveType is not (DriveType.Fixed or DriveType.Removable))
            {
                return new VolumeFacts(true, volume, volumePath, 0, true, driveType, null);
            }

            if (!drive.IsReady)
            {
                return new VolumeFacts(true, volume, volumePath, 0, false, driveType, null);
            }

            var format = drive.DriveFormat;
            try
            {
                return new VolumeFacts(true, volume, volumePath, ReadSerial(volumePath, format), true, driveType, format);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                return new VolumeFacts(true, volume, volumePath, 0, false, driveType, format);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return new VolumeFacts(true, fallbackVolume, fallbackVolume is { } volume ? $"{volume.Letter}:\\" : null, 0,
                false, DriveType.Unknown, null);
        }
    }

    /// <summary>The drive letter when <paramref name="volumePath" /> is exactly a lettered volume root such as <c>C:\</c>.</summary>
    static VolumeKey? LetterOf(string volumePath) =>
        volumePath is [var letter, ':', '\\'] && char.IsAsciiLetter(letter) ? new VolumeKey(letter) : null;

    /// <summary>
    ///     The full serial the file system reports for the volume rooted at <paramref name="volumePath" />, read once
    ///     per open without elevation from <c>FILE_ID_INFO</c> on the root directory. On NTFS that is the 64-bit
    ///     serial the elevated scan reads from <c>FSCTL_GET_NTFS_VOLUME_DATA</c>, so an NTFS volume that refuses the
    ///     query has no serial to report and fails the read. A file system with only a 32-bit serial that refuses
    ///     the query reports its <c>GetVolumeInformationW</c> serial, zero-extended.
    /// </summary>
    /// <exception cref="IOException">The volume refused every serial query open to its file system.</exception>
    internal static ulong ReadSerial(string volumePath, string format)
    {
        using (var root = Kernel32.Hooks._createFile(volumePath, FileReadAttributes, ShareAll, IntPtr.Zero,
                   OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
        {
            if (!root.IsInvalid && Kernel32.Hooks._readVolumeSerialByHandle(root, out var fullSerial))
            {
                return fullSerial;
            }
        }

        if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase) &&
            Kernel32.Hooks._readVolumeSerialByRoot(volumePath, out var serial))
        {
            return serial;
        }

        throw new IOException($"Could not read the volume serial of '{volumePath}'.");
    }

    const uint FileReadAttributes = 0x80;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;
}
