using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Pure arithmetic on <see cref="NtfsVolumeInformation.MftRecordCount" />. The live
///     <see cref="NtfsVolumeInformation.Query" /> path needs an elevated volume handle - see
///     <see cref="NtfsVolumeInformationAdminTests" />.
/// </summary>
[TestClass]
public class NtfsVolumeInformationTests
{
    [TestMethod]
    public void MftRecordCount_DividesValidDataLengthByRecordSegmentSize()
    {
        var info = new NtfsVolumeInformation(8_192_000_000L, 1024, 512, 4096, 1_000_000, 500_000);
        Assert.AreEqual(8_000_000L, info.MftRecordCount);
    }

    [TestMethod]
    public void MftRecordCount_ZeroBytesPerFileRecordSegment_ReturnsZero_NotDivideByZero()
    {
        var info = new NtfsVolumeInformation(8_192_000_000L, 0, 512, 4096, 1_000_000, 500_000);
        Assert.AreEqual(0L, info.MftRecordCount);
    }

    [TestMethod]
    public void MftRecordCount_ZeroValidDataLength_ReturnsZero()
    {
        var info = new NtfsVolumeInformation(0, 1024, 512, 4096, 1_000_000, 500_000);
        Assert.AreEqual(0L, info.MftRecordCount);
    }

    [TestMethod]
    public void MftRecordCount_TruncatesTowardZero_LikeIntegerDivision()
    {
        // 1025 / 1024 = 1 remainder 1: the fractional record must not round up.
        var info = new NtfsVolumeInformation(1025, 1024, 0, 0, 0, 0);
        Assert.AreEqual(1L, info.MftRecordCount);
    }
}
