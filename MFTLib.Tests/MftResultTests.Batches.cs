using MFTLib.NativeInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class MftResultTests
{
    [TestMethod]
    public void MftVolume_EnsureCompatibleNativeAbi_ThrowsOnMismatch()
    {
        MFTLibNative.Hooks._getMftNativeAbiVersion = () => 999;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(MFTLibNative.EnsureCompatibleNativeAbi);
        Assert.Contains("ABI mismatch", ex.Message);
    }
}
