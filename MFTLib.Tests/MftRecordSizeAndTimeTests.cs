using MFTLib.NativeInterop;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class MftRecordSizeAndTimeTests
{
    [TestMethod]
    public void ExpectedAbiVersion_IsEleven()
    {
        var expectedVersion = MFTLibNative.ExpectedMftNativeAbiVersion;
        Assert.AreEqual(11u, expectedVersion);
    }

    [TestMethod]
    public void NativeCompactEntrySize_IsFiftyTwo()
    {
        var entrySize = MFTLibNative.NativeCompactEntrySize;
        Assert.AreEqual(52u, entrySize);
    }

    [TestMethod]
    [TestCategory("Smoke")]
    public void NativeLibrary_ReportsAbiVersionEleven()
    {
        Assert.AreEqual(11u, MFTLibNative.Hooks._getMftNativeAbiVersion());
    }

    [TestMethod]
    [DataRow(long.MinValue, DisplayName = "Issue655_MinimumFileTime")]
    [DataRow(-1L, DisplayName = "Issue655_NegativeFileTime")]
    [DataRow(0L, DisplayName = "Issue655_AbsentFileTime")]
    [DataRow(long.MaxValue, DisplayName = "Issue655_OverflowFileTime")]
    public void ModifiedUtc_OutOfRangeFileTime_ReadsAsMinValue(long modifiedFileTime)
    {
        var record = MftRecordFactory.Create(new MftRecordTestValues
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            FileName = "x",
            ModifiedFileTime = modifiedFileTime
        });
        Assert.AreEqual(DateTime.MinValue, record.ModifiedUtc);
    }

    [TestMethod]
    public void ModifiedUtc_ValidFileTime_RoundTrips()
    {
        var expected = DateTime.FromFileTimeUtc(MftFixtureTests.ModifiedBaseFileTime);
        var record = MftRecordFactory.Create(new MftRecordTestValues
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
        var record = MftRecordFactory.Create(new MftRecordTestValues
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
