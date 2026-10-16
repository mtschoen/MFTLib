using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftFixtureTests
{
    internal const long ModifiedBaseFileTime = 132000000000000000L;
    internal const long ModifiedStepFileTime = 10000000L;

    string _fixturePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-fixture-{Guid.NewGuid():N}.mft");
        MftVolume.GenerateFixtureMFT(_fixturePath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_fixturePath))
        {
            File.Delete(_fixturePath);
        }
    }

    [TestMethod]
    public void Fixture_Parses_EightInUseRecords()
    {
        var records = DirectParse.ParseFile(_fixturePath, out _);
        Assert.AreEqual(8, records.Length);
        CollectionAssert.AreEquivalent(
            new ulong[] { 0, 5, 6, 7, 8, 9, 10, 11 },
            records.Select(record => record.RecordNumber).ToArray());
    }

    [TestMethod]
    public void Fixture_NamesAndParents_MatchTheAuthoredTable()
    {
        var records = DirectParse.ParseFile(_fixturePath, out _)
            .ToDictionary(record => record.RecordNumber);
        Assert.AreEqual("resident.txt", records[6].FileName);
        Assert.AreEqual(5ul, records[6].ParentRecordNumber);
        Assert.AreEqual("nodata.dat", records[9].FileName);
        Assert.AreEqual(8ul, records[9].ParentRecordNumber);
        Assert.IsTrue(records[8].IsDirectory);
        Assert.IsFalse(records[7].IsDirectory);
    }

    [TestMethod]
    public void Fixture_ModifiedTime_ComesFromStandardInformation()
    {
        var records = DirectParse.ParseFile(_fixturePath, out _)
            .ToDictionary(record => record.RecordNumber);
        foreach (var (recordNumber, record) in records)
        {
            var expected = DateTime.FromFileTimeUtc(
                ModifiedBaseFileTime + (long)recordNumber * ModifiedStepFileTime);
            Assert.AreEqual(expected, record.ModifiedUtc, $"record {recordNumber}");
        }
    }

    [TestMethod]
    public void Fixture_NonResidentData_SizeComesFromTheLowestVcnZeroRun()
    {
        var records = DirectParse.ParseFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
        Assert.AreEqual(1234567L, records[7].Size);
        // Record 10's first $DATA has a nonzero lowest virtual cluster number, whose file
        // size field is not valid; the parser must take the second one.
        Assert.AreEqual(4096L, records[10].Size);
        Assert.IsTrue(records[10].SizeKnown);
    }

    [TestMethod]
    public void Fixture_NegativeNonResidentDataSize_PreservesRecordWithUnknownSize()
    {
        var records = DirectParse.ParseFile(_fixturePath, out _)
            .ToDictionary(record => record.RecordNumber);
        var record = records[11];
        Assert.AreEqual("negative-size.bin", record.FileName);
        Assert.AreEqual(8ul, record.ParentRecordNumber);
        Assert.IsFalse(record.SizeKnown, $"SizeKnown={record.SizeKnown}, Size={record.Size}");
        Assert.AreEqual(0L, record.Size);
    }

    [TestMethod]
    public void Fixture_EveryRecord_ReportsItsAuthoredSizeAndKnownFlag()
    {
        var records = DirectParse.ParseFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
        (ulong RecordNumber, long Size, bool SizeKnown)[] expected =
        [
            (0, 65536, true), (5, 0, true), (6, 37, true), (7, 1234567, true),
            (8, 0, true), (9, 0, false), (10, 4096, true), (11, 0, false)
        ];

        Assert.AreEqual(expected.Length, records.Count);
        foreach (var (recordNumber, size, sizeKnown) in expected)
        {
            Assert.AreEqual(size, records[recordNumber].Size, $"size of record {recordNumber}");
            Assert.AreEqual(sizeKnown, records[recordNumber].SizeKnown, $"SizeKnown of record {recordNumber}");
        }
    }
}
