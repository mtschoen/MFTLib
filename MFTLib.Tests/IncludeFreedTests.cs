using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The native freed branch: a scan that asks for freed rows emits the fixture's freed base records with
///     their own columns, and leaves every live row as an ordinary scan returns it.
/// </summary>
[TestClass]
[DoNotParallelize]
public class IncludeFreedTests
{
    const int RecordSize = 1024;
    string _fixturePath = null!;
    string _imagePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-freed-{Guid.NewGuid():N}.mft");
        _imagePath = Path.ChangeExtension(_fixturePath, ".img");
        MftVolume.GenerateFixtureMFT(_fixturePath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        File.Delete(_fixturePath);
        File.Delete(_imagePath);
    }

    [TestMethod]
    public void Fixture_IncludeFreed_EmitsValidatedBaseRecords()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var fixture = File.ReadAllBytes(_fixturePath);
        var ordinary = Parse(fixture, false, out _);
        Assert.IsTrue(ordinary.All(record => record.InUse));
        var records = Parse(fixture, true, out var totalRecords);
        Assert.AreEqual(24ul, totalRecords);
        var freed = records.Where(record => !record.InUse).ToArray();
        var expected = new (ulong Number, string Name, ulong Parent, ushort Sequence)[]
        {
            (12, "deleted-dir", 5, 14),
            (13, "deleted-before.txt", 12, 14),
            (14, "deleted-current.txt", 12, 15),
            (15, "deleted-live.txt", 8, 16),
            (16, "deleted-reused.txt", 8, 17),
            (17, "deleted-stale.txt", 12, 18),
            (21, "deleted-under-freed-file.txt", 13, 22),
            (22, "deleted-under-live-file.txt", 6, 23)
        };
        CollectionAssert.AreEqual(expected.Select(row => row.Number).ToArray(),
            freed.Select(record => record.RecordNumber).ToArray());
        foreach (var row in expected)
        {
            var record = freed.Single(candidate => candidate.RecordNumber == row.Number);
            Assert.AreEqual(row.Name, record.FileName);
            Assert.AreEqual(row.Parent, record.ParentRecordNumber);
            Assert.AreEqual(row.Sequence, record.SequenceNumber);
            Assert.AreEqual(row.Number == 12, record.IsDirectory);
            Assert.AreEqual(row.Number == 12 ? 0L : 37L, record.Size);
            Assert.IsTrue(record.SizeKnown);
            Assert.AreEqual(DateTime.FromFileTimeUtc(MftFixtureTests.ModifiedBaseFileTime +
                (long)row.Number * MftFixtureTests.ModifiedStepFileTime), record.ModifiedUtc);
        }

        AssertRowsEqual(ordinary, records.Where(record => record.InUse).ToArray());
        Assert.IsFalse(records.Any(record => record.RecordNumber is 18 or 19 or 20 or 23));
    }

    [TestMethod]
    public void LiveOrigin_UnderAFreedParent_KeepsItsRowWhenFreedRowsAreIncluded()
    {
        if (WindowsOnlyNative.SkipWithoutVolumeParse())
        {
            return;
        }

        var fixture = File.ReadAllBytes(_fixturePath);
        WriteParent(fixture, 9, 12, 0);

        var ordinary = Parse(fixture, false, out _);
        var includingFreed = Parse(fixture, true, out _);

        AssertRowsEqual(ordinary, includingFreed.Where(record => record.InUse).ToArray());
        Assert.AreEqual(12UL, includingFreed.Single(record => record.RecordNumber == 9).ParentRecordNumber);
    }

    MftRecord[] Parse(byte[] fixture, bool includeFreed, out ulong totalRecords)
    {
        using var result = FixtureVolume.Parse(_imagePath, fixture, includeFreed);
        totalRecords = result.TotalRecords;
        return result.ToArray();
    }

    static void AssertRowsEqual(MftRecord[] expected, MftRecord[] actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index].RecordNumber, actual[index].RecordNumber);
            Assert.AreEqual(expected[index].ParentRecordNumber, actual[index].ParentRecordNumber);
            Assert.AreEqual(expected[index].SequenceNumber, actual[index].SequenceNumber);
            Assert.AreEqual(expected[index].InUse, actual[index].InUse);
            Assert.AreEqual(expected[index].IsDirectory, actual[index].IsDirectory);
            Assert.AreEqual(expected[index].FileAttributes, actual[index].FileAttributes);
            Assert.AreEqual(expected[index].Size, actual[index].Size);
            Assert.AreEqual(expected[index].SizeKnown, actual[index].SizeKnown);
            Assert.AreEqual(expected[index].ModifiedUtc, actual[index].ModifiedUtc);
            Assert.AreEqual(expected[index].FileName, actual[index].FileName);
        }
    }

    static void WriteParent(byte[] image, int recordNumber, ulong parent, ushort parentSequence)
    {
        var referenceOffset = FileNameOffset(image, recordNumber);
        BitConverter.GetBytes(parent | ((ulong)parentSequence << 48)).CopyTo(image, referenceOffset);
    }

    static int FileNameOffset(byte[] image, int recordNumber)
    {
        var recordOffset = recordNumber * RecordSize;
        Assert.IsTrue(recordOffset + RecordSize <= image.Length, "Fixture record must exist.");
        Assert.AreEqual(0x454c4946u, BitConverter.ToUInt32(image, recordOffset), "Fixture record must have a FILE header.");
        var attributeOffset = recordOffset + BitConverter.ToUInt16(image, recordOffset + 0x14);
        while (BitConverter.ToUInt32(image, attributeOffset) != 0x30)
        {
            var attributeLength = checked((int)BitConverter.ToUInt32(image, attributeOffset + 4));
            Assert.IsTrue(attributeLength >= 24 && attributeOffset + attributeLength < recordOffset + RecordSize,
                "Fixture record must contain a bounded FileName attribute.");
            attributeOffset += attributeLength;
        }

        return attributeOffset + BitConverter.ToUInt16(image, attributeOffset + 0x14);
    }
}
