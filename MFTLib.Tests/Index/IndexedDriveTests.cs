using System.Runtime.Versioning;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class IndexedDriveTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    [DataRow("C")]
    [DataRow("C:")]
    [DataRow(@"C:\")]
    [DataRow("c:/")]
    public void FromWindowsVolume_NormalizesEverySpellingToTheSameDrive(string spelling)
    {
        using var restore = IndexedDrive.OverrideVolumeSerialReaderForTest(root =>
        {
            Assert.AreEqual(@"C:\", root);
            return 0xDEADBEEF;
        });

        var drive = IndexedDrive.FromWindowsVolume(spelling);

        Assert.AreEqual('C', drive.DriveLetter);
        Assert.AreEqual(@"C:\", drive.RootDirectory);
        Assert.AreEqual(0xDEADBEEFu, drive.VolumeSerial);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void FromWindowsVolume_ThrowsIOExceptionNamingTheDriveWhenTheQueryFails()
    {
        using var restore = IndexedDrive.OverrideVolumeSerialReaderForTest(_ => null);

        var failure = Assert.ThrowsException<IOException>(() => IndexedDrive.FromWindowsVolume("Q"));

        StringAssert.Contains(failure.Message, "Q");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    [DataRow("")]
    [DataRow("CD")]
    [DataRow("1")]
    [DataRow(@"\\server\share")]
    public void FromWindowsVolume_RejectsAnythingThatIsNotADriveLetter(string spelling)
    {
        Assert.ThrowsException<ArgumentException>(() => IndexedDrive.FromWindowsVolume(spelling));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void FromWindowsVolume_ReadsARealSerialForTheSystemDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only: GetVolumeInformationW against a real drive");
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var drive = IndexedDrive.FromWindowsVolume(systemRoot);

        Assert.AreNotEqual(0u, drive.VolumeSerial);
        Assert.AreEqual(systemRoot, drive.RootDirectory);
    }
}
