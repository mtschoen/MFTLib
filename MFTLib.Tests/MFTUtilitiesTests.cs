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
}
