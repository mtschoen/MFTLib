using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerDriveLetterTests
{
    [TestMethod]
    public void NormalizeDriveLetter_ValidDriveFormats_ReturnsUppercaseLetter()
    {
        foreach (var drive in new[] { "C", "c", "C:", "c:", @"C:\", @"c:\", "C:/", "c:/", @"\\.\C:", @"\\.\c:", @"\\.\C", "  C:  " })
        {
            Assert.AreEqual("C", BrokerDriveLetter.Normalize(drive));
        }
    }

    [TestMethod]
    public void NormalizeDriveLetter_InvalidInputs_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => BrokerDriveLetter.Normalize(null!));
        foreach (var drive in new[] { "", "   ", "C:garbage", "C,D", "1", "1:", "CD", "$$", @"\\.\Volume{12345678-1234-1234-1234-123456789012}", @"C:\dir\file.txt" })
        {
            Assert.ThrowsException<ArgumentException>(() => BrokerDriveLetter.Normalize(drive));
        }
    }
}
