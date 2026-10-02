using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeParserCoverageTests
{
    // --- mft.records.cpp line 73: TryExtractFileName's second bounds check
    // (ValueOffset + requiredNameSize > RecordLength), reached only once the
    // FileNameLength byte itself is corrupted past what the attribute's
    // actual RecordLength can hold (the ValueOffset-only check above it
    // already passes for a normal, unmodified attribute). ---

    [TestMethod]
    public void ParseFromFile_FileNameLengthOverflow_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);
            var data = File.ReadAllBytes(path);

            // Record 6: StandardInformation ends at 0x38 + 0x60 = 0x98, so FileName
            // starts at 0x98. FileNameLength lives at ValueOffset(0x18) + 64 bytes
            // into the attribute value (matches the FILE_NAME struct layout).
            const int recordOffset = 6 * 1024;
            const int fileNameAttributeOffset = recordOffset + 0x98;
            var valueOffset = data[fileNameAttributeOffset + 0x14];
            var fileNameLengthOffset = fileNameAttributeOffset + valueOffset + 64;
            data[fileNameLengthOffset] = 200; // far more units than RecordLength can hold

            File.WriteAllBytes(path, data);

            var records = MftVolume.ParseMFTFromFile(path, out _);
            Assert.IsTrue(records.Length > 0);
            Assert.IsFalse(records.Any(record => record.RecordNumber == 6));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- mft.records.cpp line 89: FindNamedAttribute's FirstAttributeOffset
    // guard (< 42, or overflowing the record). ---

    [TestMethod]
    [DataRow((ushort)10)] // below the minimum of 42
    [DataRow((ushort)1022)] // offset + sizeof(uint32_t) overflows a 1024-byte record
    public void ParseFromFile_InvalidFirstAttributeOffset_SkipsRecord(ushort firstAttributeOffset)
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024];
            WriteFileRecord(data, 0);
            data[0x14] = (byte)(firstAttributeOffset & 0xFF);
            data[0x15] = (byte)(firstAttributeOffset >> 8);

            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(1UL, result.TotalRecords);
                Assert.AreEqual(0UL, result.UsedRecords, "The record has no discoverable FileName and must be skipped");
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

    // --- mft.records.cpp line 96: the attribute-iteration bounds check,
    // reached when a well-formed first attribute's RecordLength advances the
    // walk to within 3 bytes of the record boundary (so the *next*
    // iteration's own header-read guard trips). ---

    [TestMethod]
    public void ParseFromFile_AttributeWalkOverflow_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024];
            WriteFileRecord(data, 0);

            const int attributeOffset = 0x38; // 56, matches WriteFileRecord's computed FirstAttributeOffset
            // TypeCode = ObjectId (0x40): not StandardInformation/FileName/EndMarker,
            // so it is simply skipped and the walk advances by RecordLength.
            data[attributeOffset] = 0x40;
            data[attributeOffset + 1] = 0;
            data[attributeOffset + 2] = 0;
            data[attributeOffset + 3] = 0;
            // RecordLength = 966: next offset = 56 + 966 = 1022; 1022 + 4 = 1026 > 1024.
            const int recordLength = 966;
            data[attributeOffset + 4] = recordLength & 0xFF;
            data[attributeOffset + 5] = recordLength >> 8;
            data[attributeOffset + 8] = 0; // FormCode = resident

            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(1UL, result.TotalRecords);
                Assert.AreEqual(0UL, result.UsedRecords, "The overflowing attribute walk must skip the record");
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

    // --- mft.records.cpp line 248: EnsureEntryCapacity's realloc-failure
    // branch on a subsequent slice merge during AppendSlice. The first
    // entry-array grow (1024 -> 2048 at ordinal 6) succeeds, and the second
    // grow (2048 -> 4096 at ordinal 7) fails, verifying that partial results
    // are retained alongside the "Failed to grow entry array" error message. ---

    [TestMethod]
    public void ParseFromFile_SecondEntryArrayGrowFail_ReturnsPartialResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            // Large buffer -> one chunk containing every record; on a multi-core
            // host the per-thread slices are merged sequentially, each exercising
            // AppendSlice's capacity checks.
            MftVolume.GenerateSyntheticMFT(path, 4000, 8192);

            // Countdown 7 fails the second entry-array grow during slice merge
            // (after the initial 1024 -> 2048 realloc at ordinal 6 succeeds),
            // leaving partial records in the result.
            NativeTestHooks.NativeSetAllocFailCountdown(7);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 8192);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(
                    errorMessage!.Contains("grow entry array"),
                    $"Expected entry-array growth failure, got: {errorMessage}");
                Assert.IsTrue(result.UsedRecords > 0, "Should have partial results");
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

    // A parse on one thread must number each chunk's records from that chunk's base, exactly as
    // the multi-threaded path does; a parse thread allowance of 1 takes this path in production.

    [TestMethod]
    public void ParseFromFile_SingleThreadAcrossChunks_NumbersRecordsLikeEveryCore()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 1000, 256);

            var everyCore = MftVolume.ParseMFTFromFile(path, null, MatchFlags.None, out _, 64)
                .Select(record => record.RecordNumber).ToArray();
            NativeTestHooks.NativeSetMaxThreads(1);
            var singleThread = MftVolume.ParseMFTFromFile(path, null, MatchFlags.None, out _, 64)
                .Select(record => record.RecordNumber).ToArray();

            Assert.IsTrue(everyCore.Length > 64, "The parse must span several 64-record chunks");
            CollectionAssert.AreEqual(everyCore, singleThread);
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
