using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MFTUtilitiesTests
{
    [DataTestMethod]
    [DataRow("C", @"\\.\C:")]
    [DataRow("D:", @"\\.\D:")]
    [DataRow(@"E:\", @"\\.\E:")]
    [DataRow("c", @"\\.\c:")]
    [DataRow(@"\\.\C:", @"\\.\C:")]
    [DataRow(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\", @"\\?\Volume{12345678-1234-1234-1234-123456789abc}")]
    [DataRow(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}", @"\\?\Volume{12345678-1234-1234-1234-123456789abc}")]
    public void GetVolumePath_NormalizesSupportedInput(string input, string expected)
    {
        Assert.AreEqual(expected, MFTUtilities.GetVolumePath(input));
    }

    [TestMethod]
    public void GetVolumePath_NullOrEmpty_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => MFTUtilities.GetVolumePath(null!));
        Assert.ThrowsException<ArgumentNullException>(() => MFTUtilities.GetVolumePath(string.Empty));
    }

    [DataTestMethod]
    [DataRow("   ")]
    [DataRow("not-a-volume")]
    [DataRow("invalid_path")]
    public void GetVolumePath_InvalidInput_ThrowsArgumentException(string input)
    {
        Assert.ThrowsException<ArgumentException>(() => MFTUtilities.GetVolumePath(input));
    }
}
