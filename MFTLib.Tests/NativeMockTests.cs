using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class NativeMockTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
    }

    [TestMethod]
    public void ParseMFTFromFile_NullReturn_ThrowsInvalidOperation()
    {
        MFTLibNative.ParseMFTFromFile = (_, _, _, _) => IntPtr.Zero;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.ParseMFTFromFile("fake.bin", out _));
    }

    [TestMethod]
    public void GenerateSyntheticMFT_ReturnsFalse_ThrowsInvalidOperation()
    {
        MFTLibNative.GenerateSyntheticMFT = (_, _, _) => false;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.GenerateSyntheticMFT("fake.bin", 100));
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void StreamRecords_NullReturn_ThrowsInvalidOperation()
    {
        if (!ElevationUtilities.IsElevated())
            Assert.Inconclusive("Requires admin elevation.");

        MFTLibNative.ParseMFTRecords = (_, _, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        Assert.ThrowsException<InvalidOperationException>(() =>
            volume.StreamRecords());
    }
}
