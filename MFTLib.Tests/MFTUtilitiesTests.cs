using MFTLib.Index;
using MFTLib.NativeInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MFTUtilitiesTests
{
    [TestMethod]
    [DataRow('C', @"\\.\C:")]
    [DataRow('c', @"\\.\C:")]
    [DataRow('D', @"\\.\D:")]
    public void GetVolumePath_FormatsRoot(char character, string expected)
    {
        Assert.AreEqual(expected, MFTUtilities.GetVolumePath(new VolumeKey(character)));
    }

    [TestMethod]
    public void GetVolumePath_DefaultRoot_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MFTUtilities.GetVolumePath(default));
    }
}
