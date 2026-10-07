using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- ParseMFTRecords error paths (via raw handle) ---

    [TestMethod]
    public void ParseMFTRecords_AllocFailOnResult_ReturnsNull()
    {
        NativeTestHooks.NativeSetAllocFailCountdown(1);
        var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(new IntPtr(-1), 256);
        Assert.AreEqual(IntPtr.Zero, resultPointer);
    }

    [TestMethod]
    public void ParseMFTRecords_InvalidHandle_ReturnsErrorMessage()
    {
        var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(new IntPtr(-1), 256);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            var errorMessage = result.ErrorMessage;
            Assert.IsTrue(errorMessage!.Contains("invalid"));
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }

    [TestMethod]
    public void ParseMFTRecords_NonNtfsFile_ReturnsNotNtfsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Write 4KB of garbage  -  enough for boot sector read, but not NTFS
            File.WriteAllBytes(path, new byte[4096]);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("not NTFS") || errorMessage.Contains("boot sector"));
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ParseMFTRecords_ReadFailOnBootSector_ReturnsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Fail the first Read (boot sector)
            NativeTestHooks.NativeSetReadFailCountdown(1);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("boot sector"));
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
