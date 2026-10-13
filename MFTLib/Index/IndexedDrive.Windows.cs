using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial record IndexedDrive
{
    /// <summary>
    ///     A test seam with no synchronization, which is safe only while the test host runs one
    ///     test at a time. <c>FileEntry._openById</c> is the same seam in the same shape, so
    ///     enabling test parallelism has to answer for both together.
    /// </summary>
    internal static Func<string, uint?>? _volumeSerialReaderOverride;

    /// <summary>
    ///     Builds a drive from a Windows drive letter, reading its real volume serial so a
    ///     re-lettered or replaced volume never matches the wrong cached block.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IndexedDrive FromWindowsVolume(DriveKey drive)
    {
        var rootDirectory = drive.Letter + @":\";
        var reader = _volumeSerialReaderOverride ?? ReadVolumeSerial;
        var serial = reader(rootDirectory) ?? throw new IOException(
            $"Could not read the volume serial for drive {drive}.");
        return new IndexedDrive(drive, rootDirectory, serial);
    }

    [SupportedOSPlatform("windows")]
    static uint? ReadVolumeSerial(string rootDirectory)
    {
        return GetVolumeInformationW(rootDirectory, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0)
            ? serial
            : null;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetVolumeInformationW(
        string lpRootPathName,
        IntPtr lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        IntPtr lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    internal static IDisposable OverrideVolumeSerialReaderForTest(Func<string, uint?> reader)
    {
        var previous = _volumeSerialReaderOverride;
        _volumeSerialReaderOverride = reader;
        return new RestoreVolumeSerialReader(previous);
    }

    sealed class RestoreVolumeSerialReader(Func<string, uint?>? previous) : IDisposable
    {
        public void Dispose()
        {
            _volumeSerialReaderOverride = previous;
        }
    }
}
