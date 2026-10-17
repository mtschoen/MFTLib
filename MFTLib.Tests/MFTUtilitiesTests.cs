using MFTLib.Index;
using MFTLib.NativeInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MFTUtilitiesTests
{
    [DataTestMethod]
    [DataRow('C', @"\\.\C:")]
    [DataRow('c', @"\\.\C:")]
    [DataRow('D', @"\\.\D:")]
    public void GetVolumePath_FormatsDrive(char character, string expected)
    {
        Assert.AreEqual(expected, MFTUtilities.GetVolumePath(new DriveKey(character)));
    }

    [TestMethod]
    public void GetVolumePath_DefaultDrive_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() => MFTUtilities.GetVolumePath(default));
    }
}
