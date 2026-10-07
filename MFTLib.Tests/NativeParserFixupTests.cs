using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The update sequence array of a record is untrusted: every offset and count in it is checked
///     before it is read or written through, an allocated record that fails the check fails a file
///     parse, and no hostile value reads or writes outside the record.
/// </summary>
[TestClass]
public class NativeParserFixupTests
{
    const string InvalidFixupMessage = "The dump contains an invalid MFT record fixup.";

    const int HostileRecord = 7;

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    // The offset and size written into record 7's header, for a 1024-byte record whose valid
    // values are offset 0x30 and size 3.
    public static IEnumerable<object[]> HostileArrays1024 =>
    [
        ["offset far outside the record", (ushort)0xFFF0, (ushort)3],
        ["offset at the end of the record", (ushort)1024, (ushort)3],
        ["offset in the second sector", (ushort)600, (ushort)3],
        ["offset inside the fixed header", (ushort)8, (ushort)3],
        ["offset zero", (ushort)0, (ushort)3],
        ["array truncated by the sector's last word", (ushort)506, (ushort)3],
        ["array ending exactly on the sector's last word", (ushort)505, (ushort)3],
        ["size zero", (ushort)0x30, (ushort)0],
        ["size one, no sector entries", (ushort)0x30, (ushort)1],
        ["size covering one sector of two", (ushort)0x30, (ushort)2],
        ["size covering three sectors of two", (ushort)0x30, (ushort)4],
        ["size covering more sectors than any record", (ushort)0x30, (ushort)0xFFFF],
        ["offset and size both at their maximum", (ushort)0xFFFF, (ushort)0xFFFF]
    ];

    [TestMethod]
    [DynamicData(nameof(HostileArrays1024))]
    public void Parse_AllocatedRecordWithAHostileArray_IsRejected(string description, ushort arrayOffset,
        ushort arraySize)
    {
        var dump = MftDumpFixture.Standard();
        SetArray(Record(dump, 1024, HostileRecord), arrayOffset, arraySize);

        var failure = Assert.ThrowsException<InvalidDataException>(() => ParseNames(dump), description);

        Assert.AreEqual(InvalidFixupMessage, failure.Message, description);
    }

    [TestMethod]
    [DynamicData(nameof(HostileArrays1024))]
    public void Parse_FreedRecordWithAHostileArray_IsPassedOver(string description, ushort arrayOffset,
        ushort arraySize)
    {
        var dump = MftDumpFixture.Standard();
        var record = Record(dump, 1024, HostileRecord);
        SetArray(record, arrayOffset, arraySize);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x16..], 0);

        var names = ParseNames(dump);

        CollectionAssert.DoesNotContain(names, "Notes.txt", description);
        CollectionAssert.Contains(names, "leaf.txt", description);
    }

    [TestMethod]
    [DataRow(1024, 1)]
    [DataRow(1024, 2)]
    [DataRow(4096, 1)]
    [DataRow(4096, 5)]
    [DataRow(4096, 8)]
    public void Parse_SectorTailThatIsNotTheUpdateSequenceNumber_IsRejected(int recordSize, int sector)
    {
        var dump = MftDumpFixture.Standard(recordSize);
        var tail = Record(dump, recordSize, HostileRecord).Slice(sector * 512 - 2, 2);
        tail[0] ^= 0xFF;

        var failure = Assert.ThrowsException<InvalidDataException>(() => ParseNames(dump));

        Assert.AreEqual(InvalidFixupMessage, failure.Message);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Parse_HostileArray_IsRejectedOnOneParseThreadAndOnSeveral(int parseThreads)
    {
        var dump = MftDumpFixture.Standard();
        SetArray(Record(dump, 1024, HostileRecord), 0xFFF0, 0xFFFF);
        var path = MftDumpFixture.WriteFile(_directory, dump);
        using var input = MftDumpInput.Open(path);

        var failure = MftDumpInputTests.ParseThrows<InvalidDataException>(input,
            new MftFileScanOptions(ParseThreads: new ParseThreadAllowance(parseThreads)));

        Assert.AreEqual(InvalidFixupMessage, failure.Message);
    }

    [TestMethod]
    public void Parse_Hostile4096ByteArray_IsRejected()
    {
        var dump = MftDumpFixture.Standard(4096);
        SetArray(Record(dump, 4096, HostileRecord), 0x30, 3);

        var failure = Assert.ThrowsException<InvalidDataException>(() => ParseNames(dump));

        Assert.AreEqual(InvalidFixupMessage, failure.Message);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public void Parse_ValidFixups_RestoreTheBytesTheArrayProtected(int recordSize)
    {
        // A name long enough to run across the first sector's last word, so a parse that skipped
        // the fixup would return the update sequence number inside the name.
        var longName = new string('n', 255);
        var records = MftDumpFixture.StandardRecords();
        records[HostileRecord] = new DumpRecord(longName, 6);
        var dump = MftDumpFixture.Build(recordSize, 16, records);
        var firstSectorTail = Record(dump, recordSize, HostileRecord).Slice(510, 2).ToArray();

        var names = ParseNames(dump);

        CollectionAssert.AreEqual(new byte[] { 0x01, 0x01 }, firstSectorTail, "the fixture protects the sector tail");
        CollectionAssert.Contains(names, longName);
    }

    [TestMethod]
    public void Parse_ArrayAtTheLowestOffsetAfterTheHeader_IsAccepted()
    {
        var dump = MftDumpFixture.Standard();
        var record = Record(dump, 1024, HostileRecord);
        Unprotect(record);
        SetArray(record, 0x2A, 3);
        Protect(record, 0x2A, 0x0202);

        CollectionAssert.Contains(ParseNames(dump), "Notes.txt");
    }

    string[] ParseNames(byte[] dump)
    {
        var path = MftDumpFixture.WriteFile(_directory, dump, $"{Guid.NewGuid():N}.mft");
        using var input = MftDumpInput.Open(path);
        using var result = input.Parse();
        return result.ToArray().Select(record => record.FileName).ToArray();
    }

    static Span<byte> Record(byte[] dump, int recordSize, int recordNumber) =>
        dump.AsSpan(recordNumber * recordSize, recordSize);

    static void SetArray(Span<byte> record, ushort arrayOffset, ushort arraySize)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], arrayOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], arraySize);
    }

    // Puts each sector's protected word back, the way a parse's fixup does.
    static void Unprotect(Span<byte> record)
    {
        var array = record[MftDumpFixture.UpdateSequenceArrayOffset..];
        for (var sector = 1; sector <= record.Length / 512; sector++)
        {
            array.Slice(sector * 2, 2).CopyTo(record[(sector * 512 - 2)..]);
        }
    }

    static void Protect(Span<byte> record, int arrayOffset, ushort updateSequenceNumber)
    {
        var array = record[arrayOffset..];
        BinaryPrimitives.WriteUInt16LittleEndian(array, updateSequenceNumber);
        for (var sector = 1; sector <= record.Length / 512; sector++)
        {
            var tail = record.Slice(sector * 512 - 2, 2);
            tail.CopyTo(array[(sector * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(tail, updateSequenceNumber);
        }
    }
}
