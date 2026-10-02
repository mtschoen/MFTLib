using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The parse control block: the thread allowance each chunk and path resolution read, and the
// cancellation flag read before each chunk read, after each chunk's parse, and between slices.
// Every case parses a synthetic NTFS image in 64-record chunks, so several chunks run and the
// progress callback (fired after each chunk is parsed) can only affect the chunks after it.
public partial class NativeParserCoverageTests
{
    const int ImageRecordCount = 1024;
    const int ImageChunkCount = ImageRecordCount / 64;

    static uint NativeHardwareThreadCount => NativeTestHooks.NativeGetNativeHardwareThreadCount();

    [TestMethod]
    public void NativeHardwareThreadCount_IsIndependentOfMaximumThreadCap()
    {
        var nativeHardwareThreadCount = NativeTestHooks.NativeGetNativeHardwareThreadCount();
        Console.WriteLine($"Native hardware threads: {nativeHardwareThreadCount}; managed processors: {Environment.ProcessorCount}");
        Assert.IsTrue(nativeHardwareThreadCount >= 1);

        NativeTestHooks.NativeSetMaxThreads(1);
        Assert.AreEqual(nativeHardwareThreadCount, NativeTestHooks.NativeGetNativeHardwareThreadCount());

        NativeTestHooks.NativeResetTestState();
        Assert.AreEqual(nativeHardwareThreadCount, NativeTestHooks.NativeGetNativeHardwareThreadCount());
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_AllowanceNull_EveryChunkUsesEveryCore()
    {
        WithImage(ImageRecordCount, path =>
        {
            var result = ParseImageWithoutControl(path);

            Assert.AreEqual(0u, result.Cancelled);
            AssertEveryChunkUsed(NativeHardwareThreadCount);
        });
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_AllowanceTwo_EveryChunkUsesAtMostTwoThreads()
    {
        AssertAllowanceGivesEveryChunk(2, Math.Min(2u, NativeHardwareThreadCount));
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_AllowanceAboveCores_ClampsToCores()
    {
        AssertAllowanceGivesEveryChunk((int)NativeHardwareThreadCount + 5, NativeHardwareThreadCount);
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_AllowanceZero_MeansEveryCore()
    {
        AssertAllowanceGivesEveryChunk(0, NativeHardwareThreadCount);
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_SetMaxThreadsStillCaps()
    {
        NativeTestHooks.NativeSetMaxThreads(1);
        AssertAllowanceGivesEveryChunk(4, 1);
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_AllowanceLoweredFromProgressCallback_LaterChunksUseTheNewCount()
    {
        AssertAllowanceChangedAfterFirstChunk((int)NativeHardwareThreadCount, 1);
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_AllowanceRaisedFromProgressCallback_LaterChunksUseTheNewCount()
    {
        AssertAllowanceChangedAfterFirstChunk(1, (int)NativeHardwareThreadCount);
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_PathResolutionReadsAllowanceAfterLastChunk()
    {
        WithImage(ImageRecordCount, path =>
        {
            using var control = new ParseControlBlock((int)NativeHardwareThreadCount);
            var result = ParseImage(path, MatchFlags.ResolvePaths, control, (block, phase, scanned, total) =>
            {
                if (phase == MftScanPhase.Parsing && scanned == total)
                {
                    block.Allowance = 1;
                }
            });

            Assert.AreEqual(0u, result.Cancelled);
            Assert.AreNotEqual(IntPtr.Zero, result.PathEntries, "Path resolution must have run");
            Assert.AreEqual(NativeHardwareThreadCount, ParseControlBlock.ChunkThreadCounts()[^1]);
            Assert.AreEqual(1u, NativeTestHooks.NativeGetResolveThreadCount());
        });
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_CancelledDuringPathResolution_StopsBetweenSlices()
    {
        // One resolution worker reports every 4096 entries; the image has over three times that
        // many in-use records, so an uncancelled resolution reports at least twice more.
        WithImage(16384, path =>
        {
            using var control = new ParseControlBlock(1);
            var resolvingReports = 0;
            var result = ParseImage(path, MatchFlags.ResolvePaths, control, (block, phase, _, _) =>
            {
                if (phase == MftScanPhase.ResolvingPaths && ++resolvingReports == 1)
                {
                    block.RequestCancel();
                }
            }, 4096);

            AssertCancelled(result);
            Assert.AreEqual(1, resolvingReports, "Resolution must stop at the slice after the cancelling report");
            Assert.AreEqual(1u, NativeTestHooks.NativeGetResolveThreadCount());
        });
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_CancelledBeforeFirstChunk_ReturnsCancelledWithoutReading()
    {
        WithImage(ImageRecordCount, path =>
        {
            using var control = new ParseControlBlock();
            control.RequestCancel();
            var progressCalls = 0;
            // Reads 1 and 2 are the boot sector and record 0; read 3 would be the first chunk.
            NativeTestHooks.NativeSetReadFailCountdown(3);

            var result = ParseImage(path, MatchFlags.None, control, (_, _, _, _) => progressCalls++);

            AssertCancelled(result);
            Assert.AreEqual(0, progressCalls);
            Assert.AreEqual(0, ParseControlBlock.ChunkThreadCounts().Length, "No chunk may be parsed");
            // The countdown is still armed only if no chunk was read, so the next parse's first
            // read (its boot sector) is the one that fails.
            StringAssert.Contains(ParseImageWithoutControl(path).ErrorMessage, "boot sector");
        });
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_CancelledFromProgressCallback_StopsAfterThatChunk()
    {
        WithImage(ImageRecordCount, path =>
        {
            using var control = new ParseControlBlock();
            var parsingCalls = 0;

            var result = ParseImage(path, MatchFlags.ResolvePaths, control, (block, phase, _, _) =>
            {
                if (phase == MftScanPhase.Parsing && ++parsingCalls == 1)
                {
                    block.RequestCancel();
                }
            });

            AssertCancelled(result);
            Assert.AreEqual(1, parsingCalls);
            Assert.AreEqual(1, ParseControlBlock.ChunkThreadCounts().Length);
            Assert.AreEqual(0u, NativeTestHooks.NativeGetResolveThreadCount(), "Path resolution must not run");
        });
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_CancelledDuringChunkParse_JoinsTheReadAndStopsBeforeReporting()
    {
        // One thread and 64-record chunks, so the cancellation checks run in a fixed order: 1 before
        // the first read, 2 at the top of the first chunk, 3 before its only sub-slice, 4 after its
        // parse, while the next chunk's read runs. The countdown trips check 4.
        WithImage(ImageRecordCount, path =>
        {
            using var control = new ParseControlBlock(1);
            var progressCalls = 0;
            NativeTestHooks.NativeSetCancelCheckCountdown(4);

            var result = ParseImage(path, MatchFlags.None, control, (_, _, _, _) => progressCalls++);

            AssertCancelled(result);
            Assert.AreEqual(0, progressCalls, "The parse must stop before reporting the chunk");
            CollectionAssert.AreEqual(new[] { 1u }, ParseControlBlock.ChunkThreadCounts());
        });
    }

    [TestMethod]
    public void ParseMFTRecordsWithProgress_CancelledBetweenWorkerSubSlices_StopsTheChunk()
    {
        if (NativeHardwareThreadCount < 2)
        {
            Assert.Inconclusive("Needs two parse workers");
        }

        // One 16384-record chunk on two workers, each checking before each of its two 4096-record
        // sub-slices: checks 1 and 2 precede the chunk, 3 to 6 are the workers', 7 follows the
        // parse. Each worker's second check follows its first, so the sixth check is always a
        // check between two sub-slices of one worker; it is the only one that trips.
        WithImage(16384, path =>
        {
            using var control = new ParseControlBlock(2);
            var progressCalls = 0;
            NativeTestHooks.NativeSetCancelCheckCountdown(6);

            var result = ParseImage(path, MatchFlags.None, control, (_, _, _, _) => progressCalls++, 16384);

            AssertCancelled(result);
            Assert.AreEqual(0, progressCalls);
            CollectionAssert.AreEqual(new[] { 2u }, ParseControlBlock.ChunkThreadCounts());
        });
    }

    static void AssertAllowanceGivesEveryChunk(int allowance, uint expected)
    {
        WithImage(ImageRecordCount, path =>
        {
            using var control = new ParseControlBlock(allowance);
            ParseImage(path, MatchFlags.None, control);
            AssertEveryChunkUsed(expected);
        });
    }

    static void AssertAllowanceChangedAfterFirstChunk(int initialAllowance, int laterAllowance)
    {
        WithImage(ImageRecordCount, path =>
        {
            using var control = new ParseControlBlock(initialAllowance);
            var parsingCalls = 0;
            ParseImage(path, MatchFlags.None, control, (block, phase, _, _) =>
            {
                if (phase == MftScanPhase.Parsing && ++parsingCalls == 1)
                {
                    block.Allowance = laterAllowance;
                }
            });

            var counts = ParseControlBlock.ChunkThreadCounts();
            Assert.AreEqual(ImageChunkCount, counts.Length);
            Assert.AreEqual((uint)initialAllowance, counts[0], "The chunk the callback followed keeps its count");
            CollectionAssert.AreEqual(
                Enumerable.Repeat((uint)laterAllowance, ImageChunkCount - 1).ToArray(), counts[1..]);
        });
    }

    static void AssertEveryChunkUsed(uint expected)
    {
        CollectionAssert.AreEqual(
            Enumerable.Repeat(expected, ImageChunkCount).ToArray(), ParseControlBlock.ChunkThreadCounts());
    }

    static void AssertCancelled(MftParseResult result)
    {
        Assert.AreEqual(1u, result.Cancelled);
        Assert.AreEqual("Parse cancelled", result.ErrorMessage);
        Assert.AreEqual(0UL, result.UsedRecords);
        Assert.AreEqual(IntPtr.Zero, result.Entries);
        Assert.AreEqual(IntPtr.Zero, result.PathEntries);
    }

    static void WithImage(int recordCount, Action<string> test)
    {
        var path = Path.GetTempFileName();
        try
        {
            SyntheticNtfsImage.Write(path, recordCount);
            test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    delegate void ControlProgressObserver(ParseControlBlock control, MftScanPhase phase, ulong scanned, ulong total);

    // Parses the image through the volume export with the given control block, handing that block
    // to onProgress with each progress callback, and returns a copy of the freed result's header.
    static MftParseResult ParseImage(string path, MatchFlags matchFlags, ParseControlBlock control,
        ControlProgressObserver? onProgress = null, uint bufferSizeRecords = 64)
    {
        MFTLibNative.NativeMftProgressCallback callback = (phase, scanned, total, _, _) =>
            onProgress?.Invoke(control, phase, scanned, total);
        var result = ParseVolumeExport(path, matchFlags, control.Pointer, callback, bufferSizeRecords);
        GC.KeepAlive(callback);
        return result;
    }

    static MftParseResult ParseImageWithoutControl(string path)
    {
        return ParseVolumeExport(path, MatchFlags.None, IntPtr.Zero, null, 64);
    }

    static MftParseResult ParseVolumeExport(string path, MatchFlags matchFlags, IntPtr control,
        MFTLibNative.NativeMftProgressCallback? callback, uint bufferSizeRecords)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
            fileStream.SafeFileHandle, null, matchFlags, bufferSizeRecords, control, callback);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            return Marshal.PtrToStructure<MftParseResult>(resultPointer);
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }
}
