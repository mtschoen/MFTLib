using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class MftResultTests
{
    [TestMethod]
    public void MftVolume_EnsureCompatibleNativeAbi_ThrowsOnMismatch()
    {
        MFTLibNative._getMftNativeAbiVersion = () => 999;

        var ex = Assert.ThrowsException<InvalidOperationException>(MFTLibNative.EnsureCompatibleNativeAbi);
        Assert.IsTrue(ex.Message.Contains("ABI mismatch"));
    }
}
