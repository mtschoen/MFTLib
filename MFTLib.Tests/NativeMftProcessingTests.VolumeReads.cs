using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class NativeMftProcessingTests
{
    // --- ParseMFTRecords error paths (via raw handle) ---

    [TestMethod]
    public void ParseMFTRecords_AllocFailOnResult_ReturnsNull()
    {
        NativeTestHooks.NativeSetAllocFailCountdown(1);
        var result = NativeTestHooks.ParseHandle(new IntPtr(-1));
        Assert.IsFalse(result.HasValue);
    }

    [TestMethod]
    public void ParseMFTRecords_InvalidHandle_ReturnsErrorMessage()
    {
        var result = NativeTestHooks.ParseHandle(new IntPtr(-1));
        Assert.IsTrue(result.HasValue);
        Assert.Contains("invalid", result!.Value.ErrorMessage);
    }

    [TestMethod]
    public void ParseMFTRecords_NonNtfsFile_ReturnsNotNtfsError()
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        var result = NativeTestHooks.ParseImage(new byte[4096]);
        var errorMessage = result.ErrorMessage;
        Assert.IsTrue(errorMessage!.Contains("not NTFS") || errorMessage.Contains("boot sector"));
    }

    [TestMethod]
    public void ParseMFTRecords_ReadFailOnBootSector_ReturnsError()
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        NativeTestHooks.NativeSetReadFailCountdown(1);
        var result = NativeTestHooks.ParseImage(new byte[4096]);
        Assert.Contains("boot sector", result.ErrorMessage);
    }
}
