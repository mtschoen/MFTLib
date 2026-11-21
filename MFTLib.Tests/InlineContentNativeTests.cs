using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The native reader of resident content, through a dump file so the same records exist on Windows and Linux.
///     A dump is untrusted, so each outcome is a check that fails closed: only a plain resident unnamed
///     <c>$DATA</c> of an in-use base record with the expected sequence number is ever returned.
/// </summary>
[TestClass]
public sealed class InlineContentNativeTests : IDisposable
{
    readonly TempDirectory _directory = new("mftlib-inline");

    public void Dispose() => _directory.Dispose();

    // A dump names its record size in record 0, so every dump carries one.
    string Write(int recordSize, Dictionary<int, DumpRecord> records, int recordCount = 32)
    {
        records.TryAdd(0, new DumpRecord("$MFT", 5));
        return MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(recordSize, recordCount, records));
    }

    static Dictionary<int, DumpRecord> Base(int recordSize) => new()
    {
        [0] = new DumpRecord("$MFT", 5),
        [5] = new DumpRecord(".", 5) { IsDirectory = true },
        [6] = new DumpRecord("a.txt", 5) { Content = InlineContentDump.Pattern(6, 11) },
        [7] = new DumpRecord("empty", 5) { Content = [] },
        [8] = new DumpRecord("big.bin", 5) { Content = InlineContentDump.Pattern(8, InlineContentDump.LargestContent(recordSize, "big.bin")) }
    };

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public void Read_ResidentData_ReturnsTheBytesTheRecordHolds(int recordSize)
    {
        var records = Base(recordSize);
        var path = Write(recordSize, records);

        var answers = InlineContentDump.Read(path, (6, 1), (7, 1), (8, 1));

        Assert.AreEqual(InlineContentOutcome.Returned, answers[0].Outcome);
        CollectionAssert.AreEqual(records[6].Content, answers[0].Content);
        Assert.AreEqual(InlineContentOutcome.Returned, answers[1].Outcome, "empty content is valid content");
        Assert.AreEqual(0, answers[1].Content.Length);
        Assert.AreEqual(InlineContentOutcome.Returned, answers[2].Outcome);
        CollectionAssert.AreEqual(records[8].Content, answers[2].Content,
            "the largest value includes sector end words restored from the update sequence array");
        Assert.IsGreaterThan(recordSize / 2, answers[2].Content.Length);
    }

    [TestMethod]
    public void Read_SameRecordRequestedTwice_AnswersBothFromOneRecord()
    {
        var records = Base(1024);
        var path = Write(1024, records);

        var answers = InlineContentDump.Read(path, (6, 1), (6, 1), (6, 2));

        CollectionAssert.AreEqual(records[6].Content, answers[0].Content);
        CollectionAssert.AreEqual(records[6].Content, answers[1].Content);
        Assert.AreEqual(InlineContentOutcome.Superseded, answers[2].Outcome, "the third names another incarnation");
    }

    [TestMethod]
    public void Read_NonresidentData_IsNotInline()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("big", 5) { NonResidentData = true, Size = 1 << 20 }
        });

        Assert.AreEqual(InlineContentOutcome.NotInline, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    [DataRow(0x0001, DisplayName = "compressed")]
    [DataRow(0x4000, DisplayName = "encrypted")]
    [DataRow(0x8000, DisplayName = "sparse")]
    [DataRow(0x8001, DisplayName = "sparse and compressed")]
    public void Read_ResidentDataWithAttributeFlags_IsNotInline(int flags)
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("flagged", 5) { Content = [1, 2, 3], DataFlags = (ushort)flags },
            [7] = new DumpRecord("flagged-nonresident", 5) { NonResidentData = true, Size = 99, DataFlags = (ushort)flags }
        });

        var answers = InlineContentDump.Read(path, (6, 1), (7, 1));

        Assert.AreEqual(InlineContentOutcome.NotInline, answers[0].Outcome);
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[1].Outcome);
        Assert.AreEqual(0, answers[0].Content.Length, "content is empty unless Returned");
    }

    [TestMethod]
    public void Read_ReparsePoint_IsNotInlineEvenWithResidentData()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("link", 5) { Content = [1, 2, 3], ReparsePoint = true }
        });

        Assert.AreEqual(InlineContentOutcome.NotInline, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_ReparsePointOnlyInTheFileAttributes_IsNotInlineEvenWithResidentData()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("placeholder", 5) { Content = [1, 2, 3], ReparseFileAttribute = true },
            [7] = new DumpRecord("plain", 5) { Content = [1, 2, 3] }
        });

        var answers = InlineContentDump.Read(path, (6, 1), (7, 1));

        Assert.AreEqual(InlineContentOutcome.NotInline, answers[0].Outcome,
            "the reparse file attribute alone says the content is not this value");
        Assert.AreEqual(InlineContentOutcome.Returned, answers[1].Outcome);
    }

    [TestMethod]
    public void Read_AttributeList_IsNotInlineEvenWithResidentData()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("spilled", 5) { Content = [1, 2, 3], AttributeList = true }
        });

        Assert.AreEqual(InlineContentOutcome.NotInline, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_SecondUnnamedData_IsNotInline()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("two-data", 5) { Content = [1, 2, 3], SecondUnnamedData = true }
        });

        Assert.AreEqual(InlineContentOutcome.NotInline, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_NamedStreamBeforeTheUnnamedData_ReturnsTheUnnamedValue()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("stream-first", 5) { Content = [1, 2, 3], NamedStreamBeforeData = true }
        });

        var answer = InlineContentDump.Read(path, (6, 1))[0];

        Assert.AreEqual(InlineContentOutcome.Returned, answer.Outcome);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, answer.Content, "the named stream is not the file's content");
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(0xFF)]
    public void Read_DataAttributeWithAnUnknownForm_IsUnreadable(int formCode)
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("odd-form", 5) { Content = [1, 2, 3], DataFormCode = (byte)formCode }
        });

        Assert.AreEqual(InlineContentOutcome.Unreadable, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_NonresidentDataWhoseHeaderIsTooShort_IsUnreadable()
    {
        // A file size of 0xFFFFFFFF puts an end marker where a short attribute's neighbour would start, so the
        // record is well formed except that the nonresident header does not fit inside its attribute.
        var dump = MftDumpFixture.Build(1024, 32, new Dictionary<int, DumpRecord>
        {
            [0] = new DumpRecord("$MFT", 5),
            [6] = new DumpRecord("short-header", 5) { NonResidentData = true, Size = 0xFFFFFFFFL }
        });
        var record = dump.AsSpan(6 * 1024, 1024);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record[(DataAttributeOffset(record) + 4)..], 0x38);
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        Assert.AreEqual(InlineContentOutcome.Unreadable, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_DirectoryRecordWithData_IsNotInline()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("folder", 5) { IsDirectory = true, Content = [1, 2, 3] }
        });

        Assert.AreEqual(InlineContentOutcome.NotInline, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_RecordWithoutAnUnnamedDataAttribute_IsNotInline()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("no-data", 5) { Size = null },
            [7] = new DumpRecord("stream-only", 5) { Content = [9, 9, 9], NamedStreamOnly = true }
        });

        var answers = InlineContentDump.Read(path, (6, 1), (7, 1));

        Assert.AreEqual(InlineContentOutcome.NotInline, answers[0].Outcome);
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[1].Outcome, "a named stream is not the file's content");
    }

    [TestMethod]
    public void Read_ExtensionRecord_IsNotInline()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("extension", 5) { Content = [1, 2, 3], BaseReference = (1UL << 48) | 5 }
        });

        Assert.AreEqual(InlineContentOutcome.NotInline, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_FreeRecord_IsSuperseded()
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("deleted", 5) { InUse = false, Content = [1, 2, 3] }
        });

        var answers = InlineContentDump.Read(path, (6, 1), (7, 0));

        Assert.AreEqual(InlineContentOutcome.Superseded, answers[0].Outcome, "the in-use flag is clear");
        Assert.AreEqual(InlineContentOutcome.Superseded, answers[1].Outcome, "a slot NTFS never wrote is free");
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(0)]
    [DataRow(65535)]
    public void Read_SequenceNumberOtherThanExpected_IsSuperseded(int expected)
    {
        var path = Write(1024, Base(1024));

        var answer = InlineContentDump.Read(path, (6, (ushort)expected))[0];

        Assert.AreEqual(InlineContentOutcome.Superseded, answer.Outcome);
        Assert.AreEqual(0, answer.Content.Length);
    }

    [TestMethod]
    [DataRow(32L)]
    [DataRow(33L)]
    [DataRow(1L << 40)]
    [DataRow((1L << 48) - 1)]
    public void Read_RecordPastTheEndOfTheMft_IsSuperseded(long record)
    {
        var path = Write(1024, Base(1024), recordCount: 32);

        Assert.AreEqual(InlineContentOutcome.Superseded, InlineContentDump.Read(path, (6, 1), (record, 1))[1].Outcome);
    }

    [TestMethod]
    [DataRow(510, DisplayName = "first sector")]
    [DataRow(1022, DisplayName = "second sector")]
    public void Read_RecordWithABadFixup_IsUnreadable(int tailOffset)
    {
        var dump = MftDumpFixture.Build(1024, 32, Base(1024));
        dump[(6 * 1024) + tailOffset] ^= 0xFF;
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        var answers = InlineContentDump.Read(path, (6, 1), (8, 1));

        Assert.AreEqual(InlineContentOutcome.Unreadable, answers[0].Outcome);
        Assert.AreEqual(InlineContentOutcome.Returned, answers[1].Outcome, "the damaged record does not poison its neighbours");
    }

    [TestMethod]
    public void Read_RecordWithAZeroedSignatureAndOtherBytes_IsUnreadableNotFree()
    {
        var dump = MftDumpFixture.Build(1024, 32, Base(1024));
        dump.AsSpan(6 * 1024, 4).Clear();
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        var answers = InlineContentDump.Read(path, (6, 1), (7, 1), (31, 1));

        Assert.AreEqual(InlineContentOutcome.Unreadable, answers[0].Outcome, "damage is not a free slot");
        Assert.AreEqual(InlineContentOutcome.Returned, answers[1].Outcome);
        Assert.AreEqual(InlineContentOutcome.Superseded, answers[2].Outcome, "a slot zero throughout is free");
    }

    [TestMethod]
    public void Read_RecordWithAnotherSignature_IsUnreadable()
    {
        var dump = MftDumpFixture.Build(1024, 32, Base(1024));
        "BAAD"u8.CopyTo(dump.AsSpan(6 * 1024));
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        Assert.AreEqual(InlineContentOutcome.Unreadable, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    [DataRow(1u, 0x18, DisplayName = "one byte past the attribute")]
    [DataRow(0x10000u, 0x18, DisplayName = "far past the record")]
    [DataRow(0u, 0x7000, DisplayName = "value offset outside the attribute")]
    [DataRow(0u, 0x0010, DisplayName = "value offset inside the attribute header")]
    public void Read_ValueOutsideItsAttribute_IsUnreadable(uint excess, int valueOffset)
    {
        var path = Write(1024, new Dictionary<int, DumpRecord>
        {
            [6] = new DumpRecord("bad-bounds", 5)
            {
                Content = [1, 2, 3, 4, 5, 6, 7, 8],
                ValueLengthExcess = excess,
                ValueOffset = (ushort)valueOffset
            }
        });

        Assert.AreEqual(InlineContentOutcome.Unreadable, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_ValueLengthFarPastTheRecord_IsUnreadable()
    {
        var dump = MftDumpFixture.Build(1024, 32, Base(1024));
        var record = dump.AsSpan(8 * 1024, 1024);
        var attribute = DataAttributeOffset(record);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record[(attribute + 0x10)..], 0x7FFFFFFF);
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        Assert.AreEqual(InlineContentOutcome.Unreadable, InlineContentDump.Read(path, (8, 1))[0].Outcome);
    }

    [TestMethod]
    public void Read_RecordWhoseAttributesRunPastTheRecord_IsUnreadable()
    {
        var dump = MftDumpFixture.Build(1024, 32, Base(1024));
        var record = dump.AsSpan(6 * 1024, 1024);
        var attribute = DataAttributeOffset(record);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record[(attribute + 4)..], 0x10000);
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        Assert.AreEqual(InlineContentOutcome.Unreadable, InlineContentDump.Read(path, (6, 1))[0].Outcome);
    }

    [TestMethod]
    [DataRow(0u, false, DisplayName = "zero used bytes")]
    [DataRow(0u, true, DisplayName = "used bytes that end where $DATA starts")]
    [DataRow(1025u, false, DisplayName = "past the allocated record")]
    [DataRow(uint.MaxValue, false, DisplayName = "unbounded used bytes")]
    public void Read_DataOutsideTheRecordsUsedRegion_IsUnreadable(uint usedLength, bool endBeforeData)
    {
        var dump = MftDumpFixture.Build(1024, 32, Base(1024));
        var record = dump.AsSpan(6 * 1024, 1024);
        if (endBeforeData)
        {
            usedLength = (uint)DataAttributeOffset(record);
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record[0x18..], usedLength);
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);

        var answer = InlineContentDump.Read(path, (6, 1))[0];

        Assert.AreEqual(InlineContentOutcome.Unreadable, answer.Outcome);
        Assert.AreEqual(0, answer.Content.Length);
    }

    [TestMethod]
    public void Read_RequestsAnsweredFromOneBadAndOneGoodRecord_KeepEachPositionsOwnAnswer()
    {
        var path = Write(1024, Base(1024));

        var answers = InlineContentDump.Read(path, (8, 1), (5, 1), (6, 1), (31, 1), (7, 1));

        Assert.AreEqual(InlineContentOutcome.Returned, answers[0].Outcome);
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[1].Outcome, "the root directory");
        Assert.AreEqual(InlineContentOutcome.Returned, answers[2].Outcome);
        Assert.AreEqual(InlineContentOutcome.Superseded, answers[3].Outcome, "an unwritten slot");
        Assert.AreEqual(InlineContentOutcome.Returned, answers[4].Outcome);
        Assert.AreEqual(0, answers[4].Content.Length);
    }

    // The byte offset of the unnamed $DATA attribute in a fixture record (the record's last attribute).
    static int DataAttributeOffset(Span<byte> record)
    {
        var offset = MftDumpFixture.FirstAttributeOffset(record);
        while (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]) != 0x80)
        {
            offset += (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
        }

        return offset;
    }
}
