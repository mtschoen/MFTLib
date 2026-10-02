using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

/// <summary>
///     Tests targeting native C++ code paths that are otherwise uncovered:
///     single-threaded fallback, allocation failures, Read failures, error branches.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class NativeCoverageTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
    }

    // --- Single-threaded path (ProcessRecordBatch + fallback) ---

    [TestMethod]
    public void ParseFromFile_SingleThreaded_ProducesResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            // Use 5000 records to exceed initial capacity (1024) and trigger realloc
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            NativeTestHooks.NativeSetMaxThreads(1);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(5000UL, result.TotalRecords);
                Assert.IsTrue(result.UsedRecords > 1024, "Should exceed initial capacity to trigger realloc");
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
    public void ParseFromFile_SingleThreaded_WithFilter_FiltersResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            NativeTestHooks.NativeSetMaxThreads(1);

            var resultPointer = MFTLibNative._parseMftFromFile(path, ".git", MatchFlags.ExactMatch, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(5000UL, result.TotalRecords);
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
    public void ParseFromFile_SingleThreaded_WithPaths_ResolvesPathEntries()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            NativeTestHooks.NativeSetMaxThreads(1);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
                Assert.AreNotEqual(IntPtr.Zero, result.PathEntries);
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
    public void ParseFromFile_MultiThreaded_WithPaths_ResolvesPathEntries()
    {
        // Default thread count (>1) exercises the parallel path-resolution branch;
        // the single-threaded variant above covers the serial fallback.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
                Assert.AreNotEqual(IntPtr.Zero, result.PathEntries);
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
    public void ParseFromFile_DeepHierarchy_ResolvesLongPathBeyond1024Units()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            var data = File.ReadAllBytes(path);
            var chain = new List<(ulong RecordNumber, int NameLength)>();
            ulong parentRecord = 5;
            for (var recordNumber = 6; recordNumber < 5000 && chain.Count < 128; recordNumber++)
            {
                if (!TrySetParentRecord(data, recordNumber, parentRecord, out var nameLength) || nameLength < 8)
                {
                    continue;
                }

                chain.Add(((ulong)recordNumber, nameLength));
                parentRecord = (ulong)recordNumber;
            }

            Assert.AreEqual(128, chain.Count);
            var expectedLength = chain.Sum(item => item.NameLength) + chain.Count - 1;
            Assert.IsTrue(expectedLength >= 1024);
            File.WriteAllBytes(path, data);

            var records = MftVolume.ParseMFTFromFile(path, null, MatchFlags.ResolvePaths, out _);
            var deepest = records.Single(record => record.RecordNumber == chain[^1].RecordNumber);
            Assert.IsNotNull(deepest.FullPath);
            Assert.AreEqual(expectedLength, deepest.FullPath.Length);
            Assert.IsTrue(deepest.FullPath.Length >= 1024);
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
    public void ParseFromFile_NamePoolExhausted_ReportsTruncation()
    {
        // Shrink the path-name pool so names can't fit, forcing the exhaustion path
        // in PathLookup::storeName. The parse still succeeds with truncated paths,
        // but errorMessage reports the drop.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            NativeTestHooks.NativeSetNamePoolCapacityOverride(16);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage.Contains("exhausted"),
                    $"Expected pool-exhaustion message, got: {result.ErrorMessage}");
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
    public void ParseFromFile_PathEntryAllocFail_LeavesPathsEmpty()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 100, 256);
            NativeTestHooks.NativeSetAllocFailCountdown(10);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
                Assert.AreEqual(IntPtr.Zero, result.PathEntries);
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
    public void ParseFromFile_FileSizeFailure_ReturnsError()
    {
        // size_of() returning < 0 after a successful open  -  defensive error path.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);
            NativeTestHooks.NativeSetFailFileSize(1);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage.Contains("file size"),
                    $"Expected file-size error, got: {result.ErrorMessage}");
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
    public void ParseFromFile_PathConversionFailure_ReturnsError()
    {
        // WideCharToMultiByte returning <= 0  -  defensive error path. The conversion
        // fails before the file is touched, so the path need not exist.
        NativeTestHooks.NativeSetFailPathConversion(1);
        var resultPointer = MFTLibNative._parseMftFromFile(@"C:\does_not_matter.mft", null, MatchFlags.None, 256);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            Assert.IsTrue(result.ErrorMessage.Contains("UTF-8"),
                $"Expected UTF-8 conversion error, got: {result.ErrorMessage}");
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }
}
