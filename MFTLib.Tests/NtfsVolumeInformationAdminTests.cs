using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Tests that require admin elevation to open a raw volume handle for
///     <see cref="NtfsVolumeInformation.Query" />.
///     Run via: scripts/run-admin-tests.ps1
/// </summary>
[TestClass]
[TestCategory("RequiresAdmin")]
public class NtfsVolumeInformationAdminTests
{
    static void RequireElevation()
    {
        if (!ElevationUtilities.IsElevated())
        {
            Assert.Inconclusive("Requires admin elevation. Run scripts/run-admin-tests.ps1");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only (FSCTL_GET_NTFS_VOLUME_DATA)
    public void Query_SystemDrive_ReturnsPlausibleVolumeGeometry()
    {
        RequireElevation();

        var info = NtfsVolumeInformation.Query("C");

        Assert.IsTrue(info.BytesPerFileRecordSegment is 1024 or 4096,
            $"Expected the standard 1024- or 4096-byte MFT record size, got {info.BytesPerFileRecordSegment}");
        Assert.IsTrue(info.MftValidDataLength > 0, "A live system volume must have a non-empty MFT");
        Assert.IsTrue(info.MftRecordCount > 0, "A live system volume must report at least one MFT record");
        Assert.IsTrue(info.BytesPerSector > 0);
        Assert.IsTrue(info.BytesPerCluster > 0);
        Assert.IsTrue(info.TotalClusters > 0);
        Assert.IsTrue(info.FreeClusters >= 0 && info.FreeClusters <= info.TotalClusters,
            $"FreeClusters ({info.FreeClusters}) must be between 0 and TotalClusters ({info.TotalClusters})");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only (FSCTL_GET_NTFS_VOLUME_DATA)
    public void Query_AcceptsSameDriveFormatsAsMftVolumeOpen()
    {
        RequireElevation();

        var fromLetter = NtfsVolumeInformation.Query("C");
        var fromColon = NtfsVolumeInformation.Query("C:");
        var fromBackslash = NtfsVolumeInformation.Query(@"C:\");

        Assert.AreEqual(fromLetter.MftRecordCount, fromColon.MftRecordCount);
        Assert.AreEqual(fromLetter.MftRecordCount, fromBackslash.MftRecordCount);
    }
}
