using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Generated images patched in place: every record of a larger image is numbered and bounded, one
// malformed attribute or name is dropped without failing the parse, and parallel chunks still
// report progress in order.
public partial class DumpParseNativeTests
{
    const int FixtureRecordSize = 1024;
    const uint DataAttributeType = 0x80;
    const uint AttributeEndMarker = 0xFFFFFFFF;

    [TestMethod]
    public void Parse_ThousandRecords_NumbersEveryRecordAndBoundsTheStringPool()
    {
        var path = WriteSynthetic(1024, 4096);

        var header = ParseHeader(path, 4096);
        var records = DirectParse.ParseFile(path, out _, 4096);

        Assert.AreEqual(1024UL, header.TotalRecords);
        Assert.IsTrue(header.UsedRecords > 0);
        Assert.AreEqual(header.UsedRecords, (ulong)records.Length);
        Assert.IsTrue(header.EntryStringUnits < header.UsedRecords * 260,
            "The string pool holds fewer than 260 units for each record.");
        foreach (var record in records)
        {
            Assert.AreEqual((ushort)(record.RecordNumber + 1), record.SequenceNumber, $"record {record.RecordNumber}");
            Assert.AreEqual((ushort)0, record.ParentSequenceNumber, $"parent of record {record.RecordNumber}");
        }
    }

    [TestMethod]
    public void Parse_NonresidentDataAttributeTooShortForItsFileSize_DropsTheRecord()
    {
        const int targetRecordNumber = 7;
        const int shortAttributeLength = 24;
        var path = Path.Combine(TemporaryDirectoryPath, "fixture.mft");
        MftFixture.Generate(path);
        var data = File.ReadAllBytes(path);
        var recordOffset = targetRecordNumber * FixtureRecordSize;
        var attributeOffset = recordOffset + FindUnnamedDataAttributeOffset(data.AsSpan(recordOffset, FixtureRecordSize));
        // The attribute is cut to 24 bytes in place, so the untouched bytes at the real FileSize offset
        // still hold 1234567: a parser without the length guard would accept it with that size.
        data.AsSpan(attributeOffset, shortAttributeLength).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(attributeOffset), DataAttributeType);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(attributeOffset + 4), shortAttributeLength);
        data[attributeOffset + 8] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(attributeOffset + shortAttributeLength),
            AttributeEndMarker);
        File.WriteAllBytes(path, data);

        var records = DirectParse.ParseFile(path, out _, 256);

        Assert.IsFalse(records.Any(record => record.RecordNumber == targetRecordNumber),
            "The malformed record must be dropped, not accepted with a stale size.");
        CollectionAssert.IsSubsetOf(new ulong[] { 0, 5, 6, 8 }, records.Select(record => record.RecordNumber).ToArray());
    }

    [TestMethod]
    public void Parse_ZeroLengthFileName_KeepsTheRecordWithAnEmptyName()
    {
        var path = WriteSynthetic(20, 256);
        var data = File.ReadAllBytes(path);
        // Record 6's $FILE_NAME value starts at 0xB0 (attribute at 0x98, value offset 0x18); its name
        // length byte is 0x40 into the value.
        data[6 * FixtureRecordSize + 0xF0] = 0;
        File.WriteAllBytes(path, data);

        var records = DirectParse.ParseFile(path, out _, 256);

        var record = records.Single(candidate => candidate.RecordNumber == 6);
        Assert.AreEqual(string.Empty, record.FileName);
    }

    [TestMethod]
    public void Parse_EightWorkersOverManyChunks_ReportsEveryChunkInOrder()
    {
        const ulong recordCount = 9000;
        const uint chunkRecords = 1024;
        var path = WriteSynthetic(recordCount, chunkRecords);
        NativeTestHooks.NativeSetMaxThreads(8);
        var reports = new List<MftScanProgress>();
        using var input = MftDumpInput.Open(path);

        using var result = input.Parse(new MftFileScanOptions(new SynchronousProgress<MftScanProgress>(reports.Add),
            BufferSizeRecords: chunkRecords));

        Assert.AreEqual(recordCount, result.TotalRecords);
        Assert.IsTrue(result.UsedRecords > 0);
        Assert.AreEqual((int)((recordCount + chunkRecords - 1) / chunkRecords), reports.Count);
        long previous = 0;
        foreach (var report in reports)
        {
            Assert.IsTrue(report.RecordsScanned > previous, $"{report.RecordsScanned} does not pass {previous}");
            Assert.IsTrue(report.RecordsScanned <= report.TotalRecords);
            Assert.AreEqual((long)recordCount, report.TotalRecords);
            previous = report.RecordsScanned;
        }

        Assert.AreEqual((long)recordCount, previous);
    }

    // Walks one on-disk record's attribute chain from its FirstAttributeOffset to the first unnamed $DATA
    // attribute; the offset depends on the fixture's layout, so it is found rather than hard-coded.
    static int FindUnnamedDataAttributeOffset(ReadOnlySpan<byte> record)
    {
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (offset + sizeof(uint) <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttributeEndMarker)
            {
                break;
            }

            if (type == DataAttributeType && record[offset + 9] == 0)
            {
                return offset;
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length == 0)
            {
                break;
            }

            offset += length;
        }

        Assert.Fail("The record has no unnamed $DATA attribute.");
        return -1;
    }
}
