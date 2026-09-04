using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftFixtureTests
{
    internal const long ModifiedBaseFileTime = 132000000000000000L;
    internal const long ModifiedStepFileTime = 10000000L;

    string _fixturePath = null!;

    internal static bool SkipOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Assert.Inconclusive("MftVolume.ParseMFTFromFile binds a Windows-only native export.");
        return true;
    }

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-fixture-{Guid.NewGuid():N}.mft");
        if (OperatingSystem.IsWindows())
        {
            MftVolume.GenerateFixtureMFT(_fixturePath);
        }
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
    public void Fixture_Parses_SevenInUseRecords()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _);
        Assert.AreEqual(7, records.Length);
        CollectionAssert.AreEquivalent(
            new ulong[] { 0, 5, 6, 7, 8, 9, 10 },
            records.Select(record => record.RecordNumber).ToArray());
    }

    [TestMethod]
    public void Fixture_NamesAndParents_MatchTheAuthoredTable()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _)
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
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _)
            .ToDictionary(record => record.RecordNumber);
        foreach (var (recordNumber, record) in records)
        {
            var expected = DateTime.FromFileTimeUtc(
                ModifiedBaseFileTime + (long)recordNumber * ModifiedStepFileTime);
            Assert.AreEqual(expected, record.ModifiedUtc, $"record {recordNumber}");
        }
    }

    [TestMethod]
    public void Fixture_ResidentData_SizeIsTheValueLength()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
        Assert.AreEqual(37L, records[6].Size);
        Assert.IsTrue(records[6].SizeKnown);
    }

    [TestMethod]
    public void Fixture_NonResidentData_SizeComesFromTheLowestVcnZeroRun()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
        Assert.AreEqual(1234567L, records[7].Size);
        // Record 10's first $DATA has a nonzero lowest virtual cluster number, whose file
        // size field is not valid; the parser must take the second one.
        Assert.AreEqual(4096L, records[10].Size);
        Assert.IsTrue(records[10].SizeKnown);
    }

    [TestMethod]
    public void Fixture_DirectoriesAndMissingData_ReportZeroWithTheRightKnownFlag()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
        Assert.AreEqual(0L, records[8].Size);
        Assert.IsTrue(records[8].SizeKnown, "a directory has a known size of zero");
        Assert.AreEqual(0L, records[9].Size);
        Assert.IsFalse(records[9].SizeKnown, "the data attribute lives in an extension record");
    }
}
