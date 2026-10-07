using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- ReadMFTRecord: Read failure during extension record ---

    [TestMethod]
    public void ParseMFTRecords_ReadFailDuringExtensionRecordRead()
    {
        // Covers line 162 (ReadMFTRecord Read failure).
        // Reads: 1=boot sector, 2=record 0, 3=ReadMFTRecord for extension → fail here
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, 1);

            WriteEndMarker(data, a2 + a2Len);
            WriteFileRecord(data, 5120, 0x0002);
            var ext1 = 5120 + 0x38;
            WriteEndMarker(data, ext1); // Just end marker  -  no Data attribute on extension record

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            NativeTestHooks.NativeSetReadFailCountdown(3); // Fail 3rd read = ReadMFTRecord
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.TotalRecords > 0);
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

    // --- VolumeReadChunk Read failure ---

    [TestMethod]
    public void ParseMFTRecords_VolumeReadChunkFail_ReturnsZeroUsedRecords()
    {
        // Covers line 950 (VolumeReadChunk returns 0 on Read failure).
        // With no attribute list, reads: 1=boot sector, 2=record 0, 3=VolumeReadChunk → fail
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            // Just a Data attribute, no AttributeList → straight to VolumeReadChunk
            var a1 = 4096 + 0x38;
            WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);
            WriteEndMarker(data, a1 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            NativeTestHooks.NativeSetReadFailCountdown(3); // Fail 3rd read = VolumeReadChunk
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.UsedRecords);
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

    // --- ReadNonResidentData failure ---

    [TestMethod]
    public void ParseMFTRecords_ReadFailDuringNonResidentAttributeList()
    {
        // Covers lines 138-140 (ReadNonResidentData Read failure).
        // Record 0 has non-resident AttributeList. Read #3 targets its data → fail.
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) → clusters 1..256
            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            // Attribute 2: Non-resident AttributeList (FormCode=1)
            var a2 = a1 + a1Len;
            // TypeCode = AttributeList (0x20)
            data[a2] = 0x20;
            // RecordLength = 72 (0x48)
            data[a2 + 4] = 0x48;
            // FormCode = 1 (non-resident)
            data[a2 + 8] = 0x01;
            // MappingPairsOffset = 0x40
            data[a2 + 0x20] = 0x40;
            // FileSize = 4096 (one cluster of attribute list data)
            var attrListSize = BitConverter.GetBytes(4096L);
            Array.Copy(attrListSize, 0, data, a2 + 0x28, 8); // AllocatedLength
            Array.Copy(attrListSize, 0, data, a2 + 0x30, 8); // FileSize
            Array.Copy(attrListSize, 0, data, a2 + 0x38, 8); // ValidDataLength
            // Data run: cluster 300, 1 cluster
            data[a2 + 0x40] = 0x11; // 1-byte length, 1-byte offset
            data[a2 + 0x41] = 0x01; // 1 cluster
            data[a2 + 0x42] = 0x80; // cluster 128  -  use unsigned to avoid sign-extend issues
            data[a2 + 0x43] = 0x00; // terminator

            WriteEndMarker(data, a2 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            NativeTestHooks.NativeSetReadFailCountdown(3); // Fail 3rd read = ReadNonResidentData
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Parse continues even if attribute list read fails
                Assert.IsTrue(result.TotalRecords > 0);
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

    // --- FreeMftResult null safety ---

    [TestMethod]
    public void FreeMftResult_NullPointer_DoesNotThrow()
    {
        MFTLibNative._freeMftResult(IntPtr.Zero);
    }

    // --- Platform pread/pwrite failure branches (platform_win32.cpp) ---

    [TestMethod]
    public void GenerateSyntheticMFT_PlatformWriteFail_ReturnsFalse()
    {
        // More than one buffer makes the generator observe the failed asynchronous
        // write before scheduling the final batch.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            NativeTestHooks.NativeSetFailPlatformWrite(1);
            var success = MFTLibNative._generateSyntheticMftSized(path, 600, 256, 1024);
            Assert.IsFalse(success, "Generation should report failure when the write fails");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- Helpers for building synthetic NTFS data ---

    static int WriteResidentAttributeList(byte[] data, int offset, params uint[] segmentNumbers)
    {
        const int entrySize = 28;
        const int valueOffset = 0x18;

        data[offset] = 0x20;
        data[offset + 8] = 0x00;
        var valueLength = entrySize * segmentNumbers.Length;
        data[offset + 0x10] = (byte)(valueLength & 0xFF);
        data[offset + 0x11] = (byte)((valueLength >> 8) & 0xFF);
        data[offset + 0x14] = valueOffset;

        for (var index = 0; index < segmentNumbers.Length; index++)
        {
            var segmentNumber = segmentNumbers[index];
            var entry = offset + valueOffset + index * entrySize;
            data[entry] = 0x80;
            data[entry + 4] = entrySize;
            data[entry + 5] = 0;
            BitConverter.GetBytes(segmentNumber).CopyTo(data, entry + 16);
        }

        var totalLength = (valueOffset + valueLength + 7) & ~7;
        data[offset + 4] = (byte)(totalLength & 0xFF);
        data[offset + 5] = (byte)((totalLength >> 8) & 0xFF);
        return totalLength;
    }
}
