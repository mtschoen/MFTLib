using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using MFTLib.NativeInterop;

namespace MFTLib.Index;

public sealed partial record IndexedRoot
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
    ///     Builds a volume from a Windows drive letter, reading its real volume serial so a
    ///     re-lettered or replaced volume never matches the wrong cached block.
    /// </summary>
    /// <param name="volume">The drive letter to read.</param>
    /// <exception cref="ArgumentException"><paramref name="volume" /> is the default key.</exception>
    /// <exception cref="IOException">The volume serial could not be read.</exception>
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification = "Windows-only pass-through to ReadWindowsVolume; the target is covered.")]
    public static IndexedRoot FromWindowsDrive(VolumeKey volume) => ReadWindowsVolume(volume);

    /// <summary>
    ///     The logic behind <see cref="FromWindowsDrive" />. The test seam answers when set; otherwise the serial
    ///     comes from <c>GetVolumeInformationW</c>, and the read throws <see cref="PlatformNotSupportedException" />
    ///     off Windows.
    /// </summary>
    internal static IndexedRoot ReadWindowsVolume(VolumeKey volume)
    {
        var rootDirectory = volume.Letter + @":\";
        var reader = Dependencies._volumeSerialReader ?? ReadVolumeSerial;
        var serial = reader(rootDirectory) ?? throw new IOException(
            $"Could not read the volume serial for volume {volume}.");
        return new IndexedRoot(volume, rootDirectory, serial);
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
