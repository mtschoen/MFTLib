using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftVolumeOpenValidationTests
{
    [TestMethod]
    public void Open_ZeroBufferSizeRecords_Throws()
    {
        var exception = Assert.ThrowsException<ArgumentOutOfRangeException>(() => MftVolume.Open("C", 0));
        Assert.AreEqual("bufferSizeRecords", exception.ParamName);
    }
}
