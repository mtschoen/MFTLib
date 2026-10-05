using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Interop;

namespace MFTLib;

/// <summary>
///     Volume-level NTFS MFT sizing, queried directly from a live volume via
///     <c>FSCTL_GET_NTFS_VOLUME_DATA</c> rather than derived from a scan. An elevated caller
///     can learn a volume's approximate MFT record count before allocating a scan buffer
///     sized for it; a standard user cannot obtain this any other way, since the same IOCTL
///     issued against a limited-access volume handle fails with
///     <c>ERROR_INVALID_FUNCTION</c> instead of returning a value.
/// </summary>
public readonly record struct NtfsVolumeInformation(
    long MftValidDataLength,
    uint BytesPerFileRecordSegment)
{
    /// <summary>
    ///     The MFT's approximate record count, derived from the bytes NTFS reports as
    ///     holding valid MFT data divided by the per-record segment size. Zero when
    ///     <see cref="BytesPerFileRecordSegment" /> is zero (an unqueried or degenerate
    ///     value), which avoids a divide-by-zero rather than reporting a spurious count.
    /// </summary>
    public long MftRecordCount => BytesPerFileRecordSegment == 0 ? 0 : MftValidDataLength / BytesPerFileRecordSegment;

    /// <summary>
    ///     Queries live NTFS volume data for <paramref name="volumePath" /> via
    ///     <c>FSCTL_GET_NTFS_VOLUME_DATA</c>. Accepts the formats <see cref="MftVolume.Open(string,uint)" />
    ///     does: a drive letter (<c>C</c>, <c>C:</c> or <c>C:\</c>), a raw device path (<c>\\.\C:</c>) or
    ///     a volume GUID path (<c>\\?\Volume{guid}</c>, with or without a trailing backslash). Opening the volume handle requires
    ///     <c>GENERIC_READ</c>, which in turn requires administrator elevation on a live
    ///     volume; throws <see cref="Win32Exception" /> on failure, including
    ///     access-denied when not elevated.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static NtfsVolumeInformation Query(string volumePath) => Query(volumePath, OperatingSystem.IsWindows());

    internal static NtfsVolumeInformation Query(string volumePath, bool isWindows)
    {
        if (!isWindows)
        {
            throw new PlatformNotSupportedException(
                "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).");
        }

        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        using var handle = FileUtilities._getVolumeHandle(normalizedPath);

        var bufferSize = Marshal.SizeOf<NtfsVolumeDataBufferNative>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var succeeded = Kernel32._deviceIoControl(
                handle, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var native = Marshal.PtrToStructure<NtfsVolumeDataBufferNative>(buffer);
            return new NtfsVolumeInformation(native.MftValidDataLength, native.BytesPerFileRecordSegment);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    const uint FsctlGetNtfsVolumeData = 0x00090064;
}
