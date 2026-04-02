using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class MFTUtilitiesTests
{
    [TestMethod]
    public void GetFileNameForDriveLetter_ValidLetter_ReturnsVolumePath()
    {
        var result = MFTUtilities.GetFileNameForDriveLetter("C");
        Assert.AreEqual(@"\\.\C:", result);
    }

    [TestMethod]
    public void GetFileNameForDriveLetter_LowercaseLetter_ReturnsVolumePath()
    {
        var result = MFTUtilities.GetFileNameForDriveLetter("g");
        Assert.AreEqual(@"\\.\g:", result);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void GetFileNameForDriveLetter_WithColon_Throws()
    {
        MFTUtilities.GetFileNameForDriveLetter("C:");
    }

    [TestMethod]
    public void GetVolumePath_SingleLetter_ReturnsVolumePath()
    {
        Assert.AreEqual(@"\\.\C:", MFTUtilities.GetVolumePath("C"));
    }

    [TestMethod]
    public void GetVolumePath_LetterWithColon_ReturnsVolumePath()
    {
        Assert.AreEqual(@"\\.\D:", MFTUtilities.GetVolumePath("D:"));
    }

    [TestMethod]
    public void GetVolumePath_LetterWithColonBackslash_ReturnsVolumePath()
    {
        Assert.AreEqual(@"\\.\E:", MFTUtilities.GetVolumePath(@"E:\"));
    }

    [TestMethod]
    public void GetVolumePath_LowercaseLetter_ReturnsVolumePath()
    {
        Assert.AreEqual(@"\\.\c:", MFTUtilities.GetVolumePath("c"));
    }

    [TestMethod]
    public void GetVolumePath_VolumeGuid_ReturnsWithoutTrailingSlash()
    {
        var guid = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}\";
        Assert.AreEqual(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}", MFTUtilities.GetVolumePath(guid));
    }

    [TestMethod]
    public void GetVolumePath_VolumeGuidNoTrailingSlash_ReturnsSame()
    {
        var guid = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}";
        Assert.AreEqual(guid, MFTUtilities.GetVolumePath(guid));
    }

    [TestMethod]
    public void GetVolumePath_RawPath_ReturnsSame()
    {
        Assert.AreEqual(@"\\.\C:", MFTUtilities.GetVolumePath(@"\\.\C:"));
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void GetVolumePath_Null_Throws()
    {
        MFTUtilities.GetVolumePath(null!);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void GetVolumePath_Empty_Throws()
    {
        MFTUtilities.GetVolumePath("");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void GetVolumePath_InvalidInput_Throws()
    {
        MFTUtilities.GetVolumePath("not-a-volume");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void GetFileNameForDriveLetter_MultiChar_Throws()
    {
        MFTUtilities.GetFileNameForDriveLetter("CD");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void GetFileNameForDriveLetter_Empty_Throws()
    {
        MFTUtilities.GetFileNameForDriveLetter("");
    }
}
