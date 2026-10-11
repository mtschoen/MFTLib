using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- Variable record size and geometry validation tests ---

    [TestMethod]
    public void ParseMFTRecords_VolumeRecordSizeOverride_InvalidSize_ReturnsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Override with unsupported record size 1536
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1536);

            var data = BuildBootSector();
            WriteFileRecord(data, 4096);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage.Contains("record size", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ParseMFTRecords_VolumeRecordSizeOverride_Supported1024_Succeeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);

            var data = BuildBootSector();
            WriteFileRecord(data, 4096, recordSize: 1024);
            var dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ParseMFTRecords_VolumeRecordSizeOverride_Supported4096_Succeeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(4096);

            var data = BuildBootSector();
            WriteFileRecord(data, 4096, 0x0001, 4096);
            var dataAttribute = 4096 + 0x48;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.AreEqual(256UL, result.TotalRecords);
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ResetTestState_ClearsVolumeRecordSizeOverride()
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1536);
            NativeTestHooks.NativeResetTestState();

            var data = BuildBootSector();
            WriteFileRecord(data, 4096, recordSize: 1024);
            var dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsFalse(result.ErrorMessage.Contains("record size", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [DataTestMethod]
    [DataRow((byte)0xF6, true, DisplayName = "-10 declares 1024-byte records")]
    [DataRow((byte)0x00, false, DisplayName = "zero declares no record size")]
    [DataRow((byte)0xE0, false, DisplayName = "-32 declares a size past the supported range")]
    public void ParseMFTRecords_OffWindowsWithoutAnOverride_TakesTheRecordSizeFromTheBootSector(
        byte clustersPerFileRecord, bool parses)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("On Windows the record size comes from FSCTL_GET_NTFS_VOLUME_DATA, not the boot sector.");
        }

        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            data[0x40] = clustersPerFileRecord;
            WriteFileRecord(data, 4096, recordSize: 1024);
            var dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                if (parses)
                {
                    Assert.AreEqual(string.Empty, result.ErrorMessage);
                    Assert.AreEqual(1024UL, result.TotalRecords);
                }
                else
                {
                    Assert.AreEqual("Failed to query volume record size or unsupported record size", result.ErrorMessage);
                }
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
            File.Delete(path);
        }
    }
}
