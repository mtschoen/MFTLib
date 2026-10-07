using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class MftRecordSizeAndTimeTests
{
    [TestMethod]
    public void ExpectedAbiVersion_IsSeven()
    {
        Assert.AreEqual(7u, MFTLibNative.ExpectedMftNativeAbiVersion);
    }

    [TestMethod]
    public void NativeCompactEntrySize_IsFiftyTwo()
    {
        Assert.AreEqual(52u, MFTLibNative.NativeCompactEntrySize);
    }

    [TestMethod]
    public void NativeLibrary_ReportsAbiVersionSeven()
    {
        Assert.AreEqual(7u, MFTLibNative._getMftNativeAbiVersion());
    }

    [TestMethod]
    public void ModifiedUtc_OutOfRangeFileTime_ReadsAsMinValue()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            FileName = "x",
            ModifiedFileTime = long.MinValue
        });
        Assert.AreEqual(DateTime.MinValue, record.ModifiedUtc);
    }

    [TestMethod]
    public void ModifiedUtc_ValidFileTime_RoundTrips()
    {
        var expected = DateTime.FromFileTimeUtc(MftFixtureTests.ModifiedBaseFileTime);
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            FileName = "x",
            FileAttributes = FileAttributes.Archive,
            ModifiedFileTime = MftFixtureTests.ModifiedBaseFileTime
        });
        Assert.AreEqual(expected, record.ModifiedUtc);
        Assert.AreEqual(FileAttributes.Archive, record.FileAttributes);
    }

    [TestMethod]
    public void SizeKnown_IsFalse_WhenTheSizeUnknownFlagIsSet()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            SizeKnown = false,
            FileName = "x",
            Size = 42
        });
        Assert.IsFalse(record.SizeKnown);
        Assert.IsTrue(record.InUse);
        Assert.AreEqual(42, record.Size);
    }
}
