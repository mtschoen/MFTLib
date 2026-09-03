using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordSizeAndTimeTests
{
    [TestMethod]
    public void ExpectedAbiVersion_IsFour()
    {
        Assert.AreEqual(4u, MFTLibNative.ExpectedMftNativeAbiVersion);
    }

    [TestMethod]
    public void NativeCompactEntrySize_IsFortyEight()
    {
        Assert.AreEqual(48u, MFTLibNative.NativeCompactEntrySize);
    }

    [TestMethod]
    public void NativeLibrary_ReportsAbiVersionFour()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        Assert.AreEqual(4u, MFTLibNative._getMftNativeAbiVersion());
    }

    [TestMethod]
    public void ModifiedUtc_OutOfRangeFileTime_ReadsAsMinValue()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            Flags = 1,
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
            Flags = 1,
            FileName = "x",
            FullPath = @"C:\x",
            FileAttributes = FileAttributes.Archive,
            ModifiedFileTime = MftFixtureTests.ModifiedBaseFileTime
        });
        Assert.AreEqual(expected, record.ModifiedUtc);
        Assert.AreEqual(@"C:\x", record.FullPath);
        Assert.AreEqual(FileAttributes.Archive, record.FileAttributes);
    }

    [TestMethod]
    public void SizeKnown_IsFalse_WhenTheSizeUnknownFlagIsSet()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            Flags = 0x8001,
            FileName = "x",
            Size = 42
        });
        Assert.IsFalse(record.SizeKnown);
        Assert.IsTrue(record.InUse);
        Assert.AreEqual(42, record.Size);
    }
}
