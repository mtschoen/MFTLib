using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="NtfsVolumeInformation.Query" /> with the <c>FileUtilities._getVolumeHandle</c>
///     and <c>Kernel32._deviceIoControl</c> seams feeding a synthetic
///     <c>FSCTL_GET_NTFS_VOLUME_DATA</c> answer, so the marshaling and error paths run
///     unelevated. The live, elevated query is covered by
///     <see cref="NtfsVolumeInformationAdminTests" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NtfsVolumeInformationQueryTests
{
    [TestCleanup]
    public void Cleanup()
    {
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only
    public void Query_WithSyntheticVolumeData_MapsEveryGeometryField()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("FSCTL_GET_NTFS_VOLUME_DATA requires Windows");
            return;
        }

        var native = new NtfsVolumeDataBufferNative
        {
            MftValidDataLength = 8_192_000_000L,
            BytesPerFileRecordSegment = 1024,
            BytesPerSector = 512,
            BytesPerCluster = 4096,
            TotalClusters = 1_000_000,
            FreeClusters = 500_000
        };
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            Assert.AreEqual(0x00090064u, ioControlCode, "FSCTL_GET_NTFS_VOLUME_DATA");
            Marshal.StructureToPtr(native, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        }

        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Kernel32._deviceIoControl = FakeDeviceIoControl;

        var info = NtfsVolumeInformation.Query("C");

        Assert.AreEqual(8_192_000_000L, info.MftValidDataLength);
        Assert.AreEqual(1024u, info.BytesPerFileRecordSegment);
        Assert.AreEqual(512u, info.BytesPerSector);
        Assert.AreEqual(4096u, info.BytesPerCluster);
        Assert.AreEqual(1_000_000L, info.TotalClusters);
        Assert.AreEqual(500_000L, info.FreeClusters);
        Assert.AreEqual(8_000_000L, info.MftRecordCount);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only
    public void Query_WhenTheIoctlFails_ThrowsWin32Exception()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("FSCTL_GET_NTFS_VOLUME_DATA requires Windows");
            return;
        }

        static bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            bytesReturned = 0;
            return false;
        }

        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Kernel32._deviceIoControl = FakeDeviceIoControl;

        Assert.ThrowsException<Win32Exception>(() => NtfsVolumeInformation.Query("C"));
    }
}
