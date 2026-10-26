using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeMftProcessingTests
{
    // --- ReadNonResidentData success path ---

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ParseMFTRecords_NonResidentAttributeList_RequiresReadableData(bool failAttributeListAllocation)
    {
        // Non-resident AttributeList with data runs pointing to valid data in the file.
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) -> clusters 1..256 (1MB)
            const int a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            // Attribute 2: Non-resident AttributeList with data at cluster 260
            // The data at cluster 260 is all zeros -> empty/invalid entries -> no extensions
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
            // Data run: 1 cluster at cluster 128 (offset 524,288, within our 2MB file)
            data[a2 + 0x40] = 0x21; // 1-byte length, 2-byte offset
            data[a2 + 0x41] = 0x01; // 1 cluster
            data[a2 + 0x42] = 0x80;
            data[a2 + 0x43] = 0x00; // cluster 128
            data[a2 + 0x44] = 0x00; // terminator

            WriteEndMarker(data, a2 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (failAttributeListAllocation)
            {
                NativeTestHooks.NativeSetAllocFailCountdown(2);
            }

            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                if (failAttributeListAllocation)
                {
                    Assert.AreEqual("The volume could not be read completely.", result.ErrorMessage);
                    Assert.AreEqual(0UL, result.UsedRecords);
                    Assert.AreEqual(IntPtr.Zero, result.Entries);
                }
                else
                {
                    Assert.AreEqual(string.Empty, result.ErrorMessage);
                    Assert.AreEqual(1024UL, result.TotalRecords);
                }
            }
            finally
            {
                MFTLibNative.Hooks._freeMftResult(resultPointer);
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
            const int dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            var attributeList = dataAttribute + dataLength;
            var attributeListLength = WriteResidentAttributeList(data, attributeList, 1, 2, 1);
            WriteEndMarker(data, attributeList + attributeListLength);

            WriteFileRecord(data, 5120, 2);
            WriteEndMarker(data, 5120 + 0x38);
            WriteFileRecord(data, 6144, 3);
            WriteEndMarker(data, 6144 + 0x38);
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.AreEqual(1024UL, result.TotalRecords);
            }
            finally
            {
                MFTLibNative.Hooks._freeMftResult(resultPointer);
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
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void ParseMFTRecords_MalformedExtensionRecord_FailsTheParse(int malformedKind)
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);
            const int dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            var attributeList = dataAttribute + dataLength;
            var attributeListLength = WriteResidentAttributeList(data, attributeList, 1);
            WriteEndMarker(data, attributeList + attributeListLength);

            const int extensionRecord = 5120;
            if (malformedKind != 0)
            {
                WriteFileRecord(data, extensionRecord, 2);
                const int extensionAttribute = extensionRecord + 0x38;
                data[extensionAttribute] = 0x80;
                if (malformedKind == 2)
                {
                    data[extensionAttribute + 4] = 0x18;
                    data[extensionAttribute + 8] = 1; // nonresident header cannot fit
                    WriteEndMarker(data, extensionAttribute + 0x18);
                }
                else if (malformedKind == 3)
                {
                    WriteEndMarker(data, extensionAttribute);
                    data[extensionRecord + 510] = 0;
                }
            }

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var expected = malformedKind switch
                {
                    0 => "Invalid MFT extension record magic",
                    3 => "MFT extension record has an invalid fixup",
                    _ => "MFT extension record has a malformed attribute"
                };
                Assert.AreEqual(expected, result.ErrorMessage);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.Entries);
            }
            finally
            {
                MFTLibNative.Hooks._freeMftResult(resultPointer);
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

    // --- Extension record Data attribute parsing ---

    [TestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, true, true, false)]
    [DataRow(true, false, false, true)]
    public void ParseMFTRecords_ResidentAttributeList_WithExtensionRecord(
        bool metadataFirst, bool reverseDataOrder, bool uncoveredData, bool namedData)
    {
        // Record 1 reaches records 64..67. An earlier metadata reference must not
        // prevent that merge; a second data extension at record 64 requires VCN ordering.
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();

            // --- Record 0 at offset 4096 ---
            WriteFileRecord(data, 4096);

            // The base run covers records 0..63.
            const int a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 64L * 1024, 1, 16);

            // Use the existing list writer, then give each entry its type and lowest VCN.
            var a2 = a1 + a1Len;
            var secondDataRecord = uncoveredData ? 9999U : 64U;
            var metadataRecord = namedData ? 9999U : 65U;
            var references = reverseDataOrder
                ? metadataFirst
                    ? new[] { metadataRecord, secondDataRecord, 1U, 0U }
                    : [secondDataRecord, 1U, metadataRecord, 0U]
                : metadataFirst
                    ? [metadataRecord, 1U, 0U]
                    : [1U, metadataRecord, 0U];
            var a2Len = WriteResidentAttributeList(data, a2, references);
            for (var index = 0; index < references.Length; index++)
            {
                var entry = a2 + 0x18 + (index * 28);
                if (references[index] == metadataRecord)
                {
                    data[entry] = namedData ? (byte)0x80 : (byte)0x30;
                    data[entry + 6] = namedData ? (byte)1 : (byte)0;
                    data[entry + 7] = namedData ? (byte)26 : (byte)0;
                    data[entry + 26] = namedData ? (byte)'x' : (byte)0;
                }
                else
                {
                    var lowestVcn = references[index] == 1 ? 16L : references[index] == 0 ? 0L : 17L;
                    BitConverter.GetBytes(lowestVcn).CopyTo(data, entry + 8);
                }
            }

            // End marker
            WriteEndMarker(data, a2 + a2Len);

            // --- Record 1 at offset 5120 (4096 + 1024) ---
            WriteFileRecord(data, 5120, 0x0002);

            // Extension record Data attribute at cluster 100 adds four records.
            const int ext1 = 5120 + 0x38;
            var ext1Len = WriteNonResidentDataAttribute(data, ext1, 4096, 100, 1);
            WriteEndMarker(data, ext1 + ext1Len);

            WriteFileRecord(data, 100 * 4096, 0x0002);
            const int secondAttribute = (100 * 4096) + 0x38;
            var secondLength = reverseDataOrder
                ? WriteNonResidentDataAttribute(data, secondAttribute, 4096, 101, 1)
                : 0;
            WriteEndMarker(data, secondAttribute + secondLength);
            WriteFileRecord(data, (100 * 4096) + 1024, 0x0002);
            WriteEndMarker(data, (100 * 4096) + 1024 + 0x38);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                if (uncoveredData)
                {
                    Assert.AreEqual("The volume could not be read completely.", result.ErrorMessage);
                    Assert.AreEqual(0UL, result.TotalRecords);
                    Assert.AreEqual(IntPtr.Zero, result.Entries);
                }
                else
                {
                    Assert.AreEqual(string.Empty, result.ErrorMessage);
                    Assert.AreEqual(reverseDataOrder ? 72UL : 68UL, result.TotalRecords);
                }
            }
            finally
            {
                MFTLibNative.Hooks._freeMftResult(resultPointer);
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
    public void ParseMFTRecords_ExtensionRecordNotInRuns_FailsTheParse()
    {
        // AttributeList points to segment 9999 which is beyond the data runs.
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            const int a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, 9999);

            WriteEndMarker(data, a2 + a2Len);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual("The volume could not be read completely.", result.ErrorMessage);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.Entries);
            }
            finally
            {
                MFTLibNative.Hooks._freeMftResult(resultPointer);
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
