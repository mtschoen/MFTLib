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
    public void ParseFromFile_InvalidMagic_ReturnsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[2048];
            // Magic "BAAD"
            data[0] = (byte)'B';
            data[1] = (byte)'A';
            data[2] = (byte)'A';
            data[3] = (byte)'D';
            BitConverter.GetBytes(1024u).CopyTo(data, 0x1C);
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [DataTestMethod]
    [DataRow(0u)]
    [DataRow(256u)]
    [DataRow(1536u)]
    [DataRow(131072u)]
    public void ParseFromFile_UnsupportedRecordSize_ReturnsError(uint recordSize)
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[4096];
            // Magic "FILE"
            data[0] = (byte)'F';
            data[1] = (byte)'I';
            data[2] = (byte)'L';
            data[3] = (byte)'E';
            BitConverter.GetBytes(recordSize).CopyTo(data, 0x1C);
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ParseFromFile_NonMultipleFileSize_ReturnsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1500]; // Not a multiple of 1024
            data[0] = (byte)'F';
            data[1] = (byte)'I';
            data[2] = (byte)'L';
            data[3] = (byte)'E';
            BitConverter.GetBytes(1024u).CopyTo(data, 0x1C);
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

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
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
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
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
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
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
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
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
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

    [TestMethod]
    public void ParseFromFile_MalformedStandardInformationValueOffset_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);
            var data = File.ReadAllBytes(path);
            // Record 6 is at byte offset 6 * 1024 = 6144
            // StandardInformation is at offset 0x38 in record 6; its Resident.ValueOffset is at offset 0x14
            // Tamper ValueOffset to 60000 (0xEA60)
            data[6 * 1024 + 0x38 + 0x14] = 0x60;
            data[6 * 1024 + 0x38 + 0x15] = 0xEA;
            File.WriteAllBytes(path, data);

            var records = MftVolume.ParseMFTFromFile(path, out _);
            Assert.IsTrue(records.Length > 0);
            // Record 6 should have been skipped due to malformed attribute
            Assert.IsFalse(records.Any(r => r.RecordNumber == 6));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ParseFromFile_MalformedFileNameValueOffset_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);
            var data = File.ReadAllBytes(path);
            // Record 6: StandardInformation length is 0x60 (ends at 0x38 + 0x60 = 0x98)
            // FileName attribute starts at 0x98 in record 6; its Resident.ValueOffset is at offset 0x14
            // Tamper ValueOffset to 60000 (0xEA60)
            data[6 * 1024 + 0x98 + 0x14] = 0x60;
            data[6 * 1024 + 0x98 + 0x15] = 0xEA;
            File.WriteAllBytes(path, data);

            var records = MftVolume.ParseMFTFromFile(path, out _);
            Assert.IsTrue(records.Length > 0);
            // Record 6 should have been skipped due to malformed attribute
            Assert.IsFalse(records.Any(r => r.RecordNumber == 6));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
