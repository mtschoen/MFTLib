using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class IncludeFreedTests
{
    const int RecordSize = 1024;
    string _fixturePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-freed-{Guid.NewGuid():N}.mft");
        if (OperatingSystem.IsWindows())
        {
            MftVolume.GenerateFixtureMFT(_fixturePath);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        File.Delete(_fixturePath);
    }

    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(false, "deleted")]
    [DataRow(true, "deleted")]
    [DataRow(false, "deleted-before.txt")]
    [DataRow(true, "deleted-before.txt")]
    public void Fixture_IncludeFreed_EmitsValidatedBaseRecords(bool resolvePaths, string? filter)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var flags = FlagsFor(resolvePaths, filter);
        var ordinary = MftVolume.ParseMFTFromFile(_fixturePath, filter, flags, out _);
        Assert.IsTrue(ordinary.All(record => record.InUse));
        var records = MftVolume.ParseMFTFromFile(_fixturePath, filter, flags | MatchFlags.IncludeFreed, out var timings);
        Assert.AreEqual(24ul, timings.TotalRecords);
        var freed = records.Where(record => !record.InUse).ToArray();
        var expected = new (ulong Number, string Name, ulong Parent, ushort Sequence, string? Path)[]
        {
            (12, "deleted-dir", 5, 14, "deleted-dir"),
            (13, "deleted-before.txt", 12, 14, @"deleted-dir\deleted-before.txt"),
            (14, "deleted-current.txt", 12, 15, @"deleted-dir\deleted-current.txt"),
            (15, "deleted-live.txt", 8, 16, @"sub\deleted-live.txt"),
            (16, "deleted-reused.txt", 8, 17, null),
            (17, "deleted-stale.txt", 12, 18, null),
            (21, "deleted-under-freed-file.txt", 13, 22, null),
            (22, "deleted-under-live-file.txt", 6, 23, null)
        };
        var selected = expected.Where(row => filter != "deleted-before.txt" || row.Number == 13).ToArray();
        CollectionAssert.AreEqual(selected.Select(row => row.Number).ToArray(),
            freed.Select(record => record.RecordNumber).ToArray());
        foreach (var row in selected)
        {
            var record = freed.Single(candidate => candidate.RecordNumber == row.Number);
            Assert.AreEqual(row.Name, record.FileName);
            Assert.AreEqual(row.Parent, record.ParentRecordNumber);
            Assert.AreEqual(row.Sequence, record.SequenceNumber);
            Assert.AreEqual(row.Number == 12, record.IsDirectory);
            Assert.AreEqual(resolvePaths ? row.Path : null, record.FullPath);
            Assert.AreEqual(row.Number == 12 ? 0L : 37L, record.Size);
            Assert.IsTrue(record.SizeKnown);
            Assert.AreEqual(DateTime.FromFileTimeUtc(MftFixtureTests.ModifiedBaseFileTime +
                (long)row.Number * MftFixtureTests.ModifiedStepFileTime), record.ModifiedUtc);
        }

        var live = records.Where(record => record.InUse).ToArray();
        AssertRowsEqual(ordinary, live);
        Assert.IsFalse(records.Any(record => record.RecordNumber is 18 or 19 or 20 or 23));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LiveOrigin_StopsAtFreedParent_WithoutChangingItsPath(bool resolvePaths)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        WriteParent(image, 9, 12, 0);
        File.WriteAllBytes(_fixturePath, image);
        var flags = FlagsFor(resolvePaths, null);
        var ordinary = MftVolume.ParseMFTFromFile(_fixturePath, null, flags, out _);
        var includingFreed = MftVolume.ParseMFTFromFile(_fixturePath, null, flags | MatchFlags.IncludeFreed, out _);
        AssertRowsEqual(ordinary, includingFreed.Where(record => record.InUse).ToArray());
        Assert.AreEqual(resolvePaths ? "nodata.dat" : null,
            includingFreed.Single(record => record.RecordNumber == 9).FullPath);
    }

    static MatchFlags FlagsFor(bool resolvePaths, string? filter)
    {
        var flags = resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None;
        return flags | (filter == "deleted-before.txt" ? MatchFlags.ExactMatch : MatchFlags.Contains);
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
            Assert.AreEqual(expected[index].FullPath, actual[index].FullPath);
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
