using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- Allocation failure paths ---

    [TestMethod]
    public void ParseFromFile_AllocFailOnResult_ReturnsNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            // Fail the first calloc (result allocation)
            NativeTestHooks.NativeSetAllocFailCountdown(1);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256, IntPtr.Zero, null);
            Assert.AreEqual(IntPtr.Zero, resultPointer);
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
    public void ParseFromFile_AllocFailOnLookup_ReturnsErrorMessage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            // Fail the second alloc (lookup.init when resolving paths)
            NativeTestHooks.NativeSetAllocFailCountdown(2);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("path lookup"));
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
    [DataRow((uint)(MatchFlags.None), 2)]
    [DataRow((uint)(MatchFlags.ResolvePaths), 6)]
    public void ParseFromFile_AllocFailOnBuffers_ReturnsErrorMessage(uint matchFlagsValue, int allocationToFail)
    {
        var matchFlags = (MatchFlags)matchFlagsValue;
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, matchFlags, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("I/O buffers"));
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
    [DataRow((uint)(MatchFlags.None), 4)]
    [DataRow((uint)(MatchFlags.ResolvePaths), 8)]
    public void ParseFromFile_AllocFailOnEntries_ReturnsErrorMessage(uint matchFlagsValue, int allocationToFail)
    {
        var matchFlags = (MatchFlags)matchFlagsValue;
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, matchFlags, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("entry array"));
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
    [DataRow((uint)(MatchFlags.None), 5)]
    [DataRow((uint)(MatchFlags.ResolvePaths), 9)]
    public void ParseFromFile_AllocFailOnStrings_ReturnsErrorMessage(uint matchFlagsValue, int allocationToFail)
    {
        var matchFlags = (MatchFlags)matchFlagsValue;
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, matchFlags, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("string pool"));
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
    [DataRow((uint)(MatchFlags.None), 6)]
    [DataRow((uint)(MatchFlags.ResolvePaths), 10)]
    public void ParseFromFile_AllocFailOnMergeGrow_ReturnsErrorMessage(uint matchFlagsValue, int allocationToFail)
    {
        var matchFlags = (MatchFlags)matchFlagsValue;
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            // Enough in-use records that the merged entry array must grow past its
            // initial 1024 capacity. With a buffer >= the record count the whole MFT
            // is one chunk, so on a multi-core host the worker slices are combined by
            // the multi-threaded merge path, exercising its realloc-failure handler.
            MftVolume.GenerateSyntheticMFT(path, 4000, 8192);

            NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, matchFlags, 8192, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("grow entry array"));
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
    [DataRow(1)]
    [DataRow(2)]
    public void GenerateSyntheticMFT_AllocFail_ReturnsFalse(int allocationToFail)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);
            var success = MFTLibNative._generateSyntheticMftSized(path, 10, 256, 1024);
            Assert.IsFalse(success);
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
    public void GenerateSyntheticMFT_NullPath_Throws()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.GenerateSyntheticMFT(null!, 10, 256));
    }

    [TestMethod]
    public void GenerateSyntheticMFT_InvalidDirectory_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing", "fixture.mft");
        Assert.IsFalse(MFTLibNative._generateSyntheticMftSized(path, 10, 256, 1024));
    }

    [TestMethod]
    public void GenerateSyntheticMFT_PathConversionFailure_ReturnsFalse()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            NativeTestHooks.NativeSetFailPathConversion(1);
            Assert.IsFalse(MFTLibNative._generateSyntheticMftSized(path, 10, 256, 1024));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }


    // --- Read failure paths ---

    [TestMethod]
    public void ParseFromFile_ReadFail_IsRejectedAsIncompleteInput()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 100, 256);

            // Fail the first chunk read: a failed read is not the end of the file.
            NativeTestHooks.NativeSetReadFailCountdown(1);
            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.Entries);
                Assert.AreEqual("The dump file could not be read completely.", result.ErrorMessage);
                Assert.AreEqual(1u, result.InvalidInput);
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
