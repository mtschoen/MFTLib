using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class MFTParseTests
{
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void ParseMFT_InvalidHandle_Throws()
    {
        using var invalidHandle = new SafeFileHandle(IntPtr.Zero, false);
        MFTParse.ParseMFT(invalidHandle);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void DumpVolumeInfo_InvalidHandle_Throws()
    {
        using var invalidHandle = new SafeFileHandle(IntPtr.Zero, false);
        MFTParse.DumpVolumeInfo(invalidHandle);
    }
}
