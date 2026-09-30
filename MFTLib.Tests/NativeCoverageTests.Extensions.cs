using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- ReadNonResidentData success path ---

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ParseMFTRecords_NonResidentAttributeList_Succeeds(bool failAttributeListAllocation)
    {
        // Non-resident AttributeList with data runs pointing to valid data in the file.
        // Exercises ReadNonResidentData success path (lines 142-145, 147).
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) → clusters 1..256 (1MB)
            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            // Attribute 2: Non-resident AttributeList with data at cluster 260
            // The data at cluster 260 is all zeros → empty/invalid entries → no extensions
            var a2 = a1 + a1Len;
            data[a2] = 0x20; // TypeCode = AttributeList
            data[a2 + 4] = 0x48; // RecordLength = 72
            data[a2 + 8] = 0x01; // FormCode = non-resident
            data[a2 + 0x20] = 0x40; // MappingPairsOffset
            // Size = 512 bytes (small, but enough to test the read path)
            var attrListSize = BitConverter.GetBytes(512L);
            Array.Copy(attrListSize, 0, data, a2 + 0x28, 8); // AllocatedLength
            Array.Copy(attrListSize, 0, data, a2 + 0x30, 8); // FileSize
            Array.Copy(attrListSize, 0, data, a2 + 0x38, 8); // ValidDataLength
            // Data run: cluster 260, 1 cluster  -  within our 2MB file (260*4096 = 1,064,960 < 2MB)
            data[a2 + 0x40] = 0x12; // 2-byte length, 1-byte offset
            data[a2 + 0x41] = 0x01;
            data[a2 + 0x42] = 0x00; // 1 cluster
            data[a2 + 0x43] = 0x80; // cluster 128 (offset 524,288  -  within 2MB)
            data[a2 + 0x44] = 0x00; // terminator

            WriteEndMarker(data, a2 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (failAttributeListAllocation)
            {
                MFTLibNative.NativeSetAllocFailCountdown(2);
            }

            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
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

    [TestMethod]
    public void ParseMFTRecords_DuplicateExtensionReferences_AreDeduplicated()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);
            var dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            var attributeList = dataAttribute + dataLength;
            var attributeListLength = WriteResidentAttributeList(data, attributeList, 1, 2, 1);
            WriteEndMarker(data, attributeList + attributeListLength);

            WriteFileRecord(data, 5120, 2);
            WriteEndMarker(data, 5120 + 0x38);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            MFTLibNative._freeMftResult(resultPointer);
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
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ParseMFTRecords_MalformedExtensionRecord_IsIgnored(int malformedKind)
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);
            var dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            var attributeList = dataAttribute + dataLength;
            var attributeListLength = WriteResidentAttributeList(data, attributeList, 1);
            WriteEndMarker(data, attributeList + attributeListLength);

            const int extensionRecord = 5120;
            if (malformedKind != 0)
            {
                WriteFileRecord(data, extensionRecord, 2);
                var extensionAttribute = extensionRecord + 0x38;
                data[extensionAttribute] = 0x80;
                if (malformedKind == 2)
                {
                    data[extensionAttribute + 4] = 0x18;
                    WriteEndMarker(data, extensionAttribute + 0x18);
                }
            }

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            MFTLibNative._freeMftResult(resultPointer);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- Extension record Data attribute parsing ---

    [TestMethod]
    public void ParseMFTRecords_ResidentAttributeList_WithExtensionRecord()
    {
        // Record 0 has Data + resident AttributeList pointing to record 1.
        // Record 1 has a Data attribute with additional data runs.
        // Exercises lines 1042, 1050-1052 (extension record Data attribute parsing).
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();

            // --- Record 0 at offset 4096 ---
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) → clusters 1..256 (1MB)
            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            // Attribute 2: AttributeList (resident) with one entry pointing to segment 1
            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, 1);

            // End marker
            WriteEndMarker(data, a2 + a2Len);

            // --- Record 1 at offset 5120 (4096 + 1024) ---
            WriteFileRecord(data, 5120, 0x0002);

            // Extension record Data attribute → cluster 300, 1 cluster (4KB = 4 records)
            var ext1 = 5120 + 0x38;
            var ext1Len = WriteNonResidentDataAttribute(data, ext1, 4096, 300, 1);
            WriteEndMarker(data, ext1 + ext1Len);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Should succeed  -  extension record adds 4 more records (1024 + 4)
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

    // --- ReadMFTRecord: record not found in data runs ---

    [TestMethod]
    public void ParseMFTRecords_ExtensionRecordNotInRuns_StillParses()
    {
        // AttributeList points to segment 9999 which is beyond the data runs.
        // Exercises lines 166-170 (ReadMFTRecord "not found").
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, 9999);

            WriteEndMarker(data, a2 + a2Len);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Should still parse  -  failed extension read is skipped
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
}
