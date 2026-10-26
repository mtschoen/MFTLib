using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using MFTLib.NativeInterop;

namespace MFTLib.Index;

public sealed partial record IndexedDrive
{
    internal static readonly VolumeDependencies Dependencies = new();

    /// <summary>Process-global test dependencies for reading volume serials.</summary>
    internal sealed class VolumeDependencies
    {
        /// <summary>Replaces the native volume serial reader when set.</summary>
        internal Func<string, uint?>? _volumeSerialReader;

        internal void ResetToDefaults() => _volumeSerialReader = null;
    }

    /// <summary>
    ///     Builds a drive from a Windows drive letter, reading its real volume serial so a
    ///     re-lettered or replaced volume never matches the wrong cached block.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification = "Windows-only pass-through to ReadWindowsVolume; the target is covered.")]
    public static IndexedDrive FromWindowsVolume(DriveKey drive) => ReadWindowsVolume(drive);

    /// <summary>
    ///     The logic behind <see cref="FromWindowsVolume" />. The test seam answers when set; otherwise the serial
    ///     comes from <c>GetVolumeInformationW</c>, and the read throws <see cref="PlatformNotSupportedException" />
    ///     off Windows.
    /// </summary>
    internal static IndexedDrive ReadWindowsVolume(DriveKey drive)
    {
        var rootDirectory = drive.Letter + @":\";
        var reader = Dependencies._volumeSerialReader ?? ReadVolumeSerial;
        var serial = reader(rootDirectory) ?? throw new IOException(
            $"Could not read the volume serial for drive {drive}.");
        return new IndexedDrive(drive, rootDirectory, serial);
    }

    static uint? ReadVolumeSerial(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Reading a volume serial requires Windows (GetVolumeInformationW).");
        }

        return Kernel32.GetVolumeInformationW(rootDirectory, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0)
            ? serial
            : null;
    }
}
