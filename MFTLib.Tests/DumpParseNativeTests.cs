using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

/// <summary>
///     Native parser paths reached through the dump entry on every platform: the single-threaded and
///     multi-threaded chunk parse, the record and attribute walkers' bounds checks, and the parse's
///     allocation failures.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class DumpParseNativeTests
{
    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    [TestMethod]
    public void Parse_OneThread_GrowsTheEntryArrayPastItsInitialCapacity()
    {
        var path = WriteSynthetic(5000, 256);
        NativeTestHooks.NativeSetMaxThreads(1);

        var result = ParseHeader(path, 256);

        Assert.AreEqual(5000UL, result.TotalRecords);
        Assert.IsTrue(result.UsedRecords > 1024, "The entry array starts at 1024 entries and must have grown");
    }

    [TestMethod]
    public void Parse_EveryCore_GrowsEachSliceAndTheMergedArray()
    {
        // A 4096-record chunk gives each worker more rows than its initial slice capacity of a
        // quarter of its range, and the merged array outgrows its initial 1024 entries.
        var path = WriteSynthetic(5000, 4096);

        var result = ParseHeader(path, 4096);

        Assert.AreEqual(5000UL, result.TotalRecords);
        Assert.IsTrue(result.UsedRecords > 1024);
    }

    [TestMethod]
    public void Parse_InUseFlagCleared_SkipsThatRecord()
    {
        var path = WriteSynthetic(20, 256);
        var data = File.ReadAllBytes(path);
        data[10 * 1024 + 0x16] = 0;
        data[10 * 1024 + 0x17] = 0;
        File.WriteAllBytes(path, data);

        var records = DirectParse.ParseFile(path, out _);

        Assert.IsTrue(records.Length > 0);
        Assert.IsFalse(records.Any(record => record.RecordNumber == 10));
    }

    // TryExtractFileName's second bounds check (ValueOffset + requiredNameSize > RecordLength), reached
    // only once the FileNameLength byte claims more units than the attribute's RecordLength holds.
    [TestMethod]
    public void Parse_FileNameLengthOverflow_SkipsRecord()
    {
        var path = WriteSynthetic(20, 256);
        var data = File.ReadAllBytes(path);
        // Record 6: StandardInformation ends at 0x38 + 0x60 = 0x98, so FileName starts at 0x98.
        // FileNameLength lives at ValueOffset + 64 bytes into the attribute value.
        const int fileNameAttributeOffset = 6 * 1024 + 0x98;
        var valueOffset = data[fileNameAttributeOffset + 0x14];
        data[fileNameAttributeOffset + valueOffset + 64] = 200;
        File.WriteAllBytes(path, data);

        var records = DirectParse.ParseFile(path, out _);

        Assert.IsTrue(records.Length > 0);
        Assert.IsFalse(records.Any(record => record.RecordNumber == 6));
    }

    [DataTestMethod]
    [DataRow(0x38)]
    [DataRow(0x98)]
    public void Parse_MalformedResidentValueOffset_SkipsRecord(int attributeOffset)
    {
        var path = WriteSynthetic(20, 256);
        var data = File.ReadAllBytes(path);
        data[6 * 1024 + attributeOffset + 0x14] = 0x60;
        data[6 * 1024 + attributeOffset + 0x15] = 0xEA;
        File.WriteAllBytes(path, data);

        var records = DirectParse.ParseFile(path, out _);

        Assert.IsTrue(records.Length > 0);
        Assert.IsFalse(records.Any(record => record.RecordNumber == 6));
    }

    // ScanRecordAttributes' FirstAttributeOffset guard: below 42, or overflowing the record.
    [DataTestMethod]
    [DataRow((ushort)10)]
    [DataRow((ushort)1022)]
    public void Parse_InvalidFirstAttributeOffset_SkipsRecord(ushort firstAttributeOffset)
    {
        var data = new byte[1024];
        WriteFileRecord(data, 0);
        data[0x14] = (byte)(firstAttributeOffset & 0xFF);
        data[0x15] = (byte)(firstAttributeOffset >> 8);
        var path = MftDumpFixture.WriteFile(_directory, data);

        var result = ParseHeader(path, 256);

        Assert.AreEqual(1UL, result.TotalRecords);
        Assert.AreEqual(0UL, result.UsedRecords, "The record has no discoverable FileName and must be skipped");
    }

    // The attribute walk's bounds check, reached when a well-formed first attribute's RecordLength
    // advances the walk to within 3 bytes of the record boundary.
    [TestMethod]
    public void Parse_AttributeWalkOverflow_SkipsRecord()
    {
        var data = new byte[1024];
        WriteFileRecord(data, 0);
        const int attributeOffset = 0x38;
        // ObjectId (0x40) is neither StandardInformation, FileName nor EndMarker, so the walk skips it.
        data[attributeOffset] = 0x40;
        // RecordLength 966: the next offset is 56 + 966 = 1022, and 1022 + 4 passes 1024.
        const int recordLength = 966;
        data[attributeOffset + 4] = recordLength & 0xFF;
        data[attributeOffset + 5] = recordLength >> 8;
        var path = MftDumpFixture.WriteFile(_directory, data);

        var result = ParseHeader(path, 256);

        Assert.AreEqual(1UL, result.TotalRecords);
        Assert.AreEqual(0UL, result.UsedRecords, "The overflowing attribute walk must skip the record");
    }

    // A parse on one thread numbers each chunk's records from that chunk's base, exactly as the
    // multi-threaded path does; a parse thread allowance of 1 takes this path in production.
    [TestMethod]
    public void Parse_OneThreadAcrossChunks_NumbersRecordsLikeEveryCore()
    {
        var path = WriteSynthetic(1000, 256);

        var everyCore = DirectParse.ParseFile(path, out _, 64).Select(record => record.RecordNumber).ToArray();
        NativeTestHooks.NativeSetMaxThreads(1);
        var singleThread = DirectParse.ParseFile(path, out _, 64).Select(record => record.RecordNumber).ToArray();

        Assert.IsTrue(everyCore.Length > 64, "The parse must span several 64-record chunks");
        CollectionAssert.AreEqual(everyCore, singleThread);
    }

    string WriteSynthetic(ulong recordCount, uint bufferSizeRecords)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".mft");
        MftVolume.GenerateSyntheticMFT(path, recordCount, bufferSizeRecords);
        return path;
    }

    // Opens the dump, then arms allocationToFail when it is nonzero, so the countdown counts only the
    // parse's own allocations and not the open's input allocation. The caller frees the result.
    static IntPtr ParseNative(string path, uint bufferSizeRecords, int allocationToFail = 0)
    {
        using var input = MFTLibNative.OpenMftDumpInput(MFTLibNative.NullTerminatedUtf8(path), out var info);
        Assert.IsFalse(input.IsInvalid, info.ErrorMessage);
        if (allocationToFail != 0)
        {
            NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);
        }

        return MFTLibNative.ParseMftDumpInput(input, bufferSizeRecords, IntPtr.Zero, null, IntPtr.Zero);
    }

    static MftParseResult ParseHeader(string path, uint bufferSizeRecords, int allocationToFail = 0)
    {
        var pointer = ParseNative(path, bufferSizeRecords, allocationToFail);
        Assert.AreNotEqual(IntPtr.Zero, pointer);
        try
        {
            return Marshal.PtrToStructure<MftParseResult>(pointer);
        }
        finally
        {
            MFTLibNative._freeMftResult(pointer);
        }
    }
}
