using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="NtfsVolumeInformation.Query" /> with the <c>FileUtilities.Hooks._getVolumeHandle</c>
///     and <c>Kernel32.Hooks._deviceIoControl</c> seams feeding a synthetic
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
        FileUtilities.Hooks.ResetToDefaults();
        Kernel32.Hooks.ResetToDefaults();
        NativeTestHooks.NativeResetTestState();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only
    public void Query_WithSyntheticVolumeData_MapsSizingAndIdentityFields()
    {
        var native = new NtfsVolumeDataBufferNative
        {
            MftValidDataLength = 8_192_000_000L,
            BytesPerFileRecordSegment = 1024,
            VolumeSerialNumber = unchecked((long)0x8111111122222222UL)
        };
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            Assert.AreEqual(0x00090064u, ioControlCode, "FSCTL_GET_NTFS_VOLUME_DATA");
            Marshal.StructureToPtr(native, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        }

        string? openedPath = null;
        FileUtilities.Hooks._getVolumeHandle = path =>
        {
            openedPath = path;
            return new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        };
        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;

        var info = NtfsVolumeInformation.Query('c');

        Assert.AreEqual(@"\\.\C:", openedPath);
        Assert.AreEqual(8_192_000_000L, info.MftValidDataLength);
        Assert.AreEqual(1024u, info.BytesPerFileRecordSegment);
        Assert.AreEqual(0x8111111122222222UL, info.VolumeSerialNumber);
        Assert.AreEqual(8_000_000L, info.MftRecordCount);

        FileUtilities.Hooks._getVolumeHandle = _ => throw new AssertFailedException("Default volume reached volume I/O.");
        Assert.ThrowsExactly<ArgumentException>(() => NtfsVolumeInformation.Query(default, isWindows: true));
    }

    [TestMethod]
    [DataRow(0x12345678A1B2C3D4UL, "Volume is not NTFS")]
    [DataRow(0x12345678A1B2C3D5UL, "Volume C does not have the expected serial.")]
    [DataRow(0xA1B2C3D4UL, "Volume C does not have the expected serial.")]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void ReadInlineContent_OnlyAMatchingSerialReachesNativeReaderOpen(ulong indexedSerial, string expectedError)
    {
        // The native open rejects this image. A serial mismatch must be reported before that open.
        using var file = new TempFile();
        File.WriteAllBytes(file.Path, new byte[8192]);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        var path = file.Path;
        var openedHandle = IntPtr.Zero;
        var queried = false;
        FileUtilities.Hooks._getVolumeHandle = _ =>
        {
            Assert.AreEqual(IntPtr.Zero, openedHandle, "Volume reopened.");
            var handle = File.OpenHandle(path);
            openedHandle = handle.DangerousGetHandle();
            return handle;
        };
        Kernel32.Hooks._deviceIoControl = (device, code, _, _, output, _, out returned, _) =>
        {
            Assert.AreEqual(openedHandle, device.DangerousGetHandle());
            Assert.AreEqual(0x00090064u, code);
            queried = true;
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative { VolumeSerialNumber = 0x12345678A1B2C3D4 },
                output, false);
            returned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };

        InlineContentRequest[] requests = [new(0, 1, 1)];
        var error = Assert.ThrowsExactly<IOException>(() => LiveVolumeSources.ReadInlineContent('C', indexedSerial,
            requests, (_, _, _) => Assert.Fail("Unexpected answer."), CancellationToken.None));

        Assert.IsTrue(queried);
        Assert.AreEqual(expectedError, error.Message);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only
    public void Query_WhenTheIoctlFails_ThrowsWin32Exception()
    {
        static bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            bytesReturned = 0;
            return false;
        }

        FileUtilities.Hooks._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;

        Assert.ThrowsExactly<Win32Exception>(() => NtfsVolumeInformation.Query('C'));
    }
}
