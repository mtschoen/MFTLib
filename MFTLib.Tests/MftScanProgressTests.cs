using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
public class MftScanProgressTests
{
    [TestMethod]
    public void Properties_And_Equality_WorkAsExpected()
    {
        var elapsed = TimeSpan.FromMilliseconds(1234);
        var progress = new MftScanProgress(500, 1000, elapsed);

        Assert.AreEqual(MftScanPhase.Parsing, progress.Phase);
        Assert.AreEqual(500L, progress.RecordsScanned);
        Assert.AreEqual(1000L, progress.TotalRecords);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: progress.Elapsed is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, progress.Elapsed);

        var explicitPhase = new MftScanProgress(MftScanPhase.ResolvingPaths, 500, 1000, elapsed);
        Assert.AreEqual(MftScanPhase.ResolvingPaths, explicitPhase.Phase);
        Assert.AreNotEqual(progress, explicitPhase);

        var same = new MftScanProgress(500, 1000, elapsed);
        Assert.AreEqual(progress, same);
        Assert.AreEqual(progress.GetHashCode(), same.GetHashCode());

        var different = new MftScanProgress(501, 1000, elapsed);
        Assert.AreNotEqual(progress, different);
    }

    [TestMethod]
    public void Deconstruction_And_PositionalPatterns_PreserveBackwardsCompatibility()
    {
        var elapsed = TimeSpan.FromMilliseconds(1234);
        var mftProgress = new MftScanProgress(500, 1000, elapsed);

        var (scanned3, total3, elapsed3) = mftProgress;
        Assert.AreEqual(500L, scanned3);
        Assert.AreEqual(1000L, total3);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: elapsed3 is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, elapsed3);

        Assert.IsTrue(mftProgress is (500L, 1000L, _));

        var (phase4, scanned4, total4, elapsed4) = mftProgress;
        Assert.AreEqual(MftScanPhase.Parsing, phase4);
        Assert.AreEqual(500L, scanned4);
        Assert.AreEqual(1000L, total4);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: elapsed4 is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, elapsed4);

        Assert.IsTrue(mftProgress is (MftScanPhase.Parsing, 500L, 1000L, _));

        var brokerProgress = new BrokerScanProgress("C", 100, 200, 300, null, elapsed);
        Assert.AreEqual("C", brokerProgress.DriveLetter);
        Assert.AreEqual(BrokerScanPhase.Parsing, brokerProgress.Phase);
        Assert.AreEqual(100L, brokerProgress.RecordsProcessed);
        Assert.AreEqual(200L, brokerProgress.BytesProcessed);
        Assert.AreEqual(300L, brokerProgress.TotalRecords);
        Assert.IsNull(brokerProgress.TotalBytes);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: brokerProgress.Elapsed is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, brokerProgress.Elapsed);

        var (drive, records, bytes, totalRec, totalBytes, bElapsed) = brokerProgress;
        Assert.AreEqual("C", drive);
        Assert.AreEqual(100L, records);
        Assert.AreEqual(200L, bytes);
        Assert.AreEqual(300L, totalRec);
        Assert.IsNull(totalBytes);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: bElapsed is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, bElapsed);

        Assert.IsTrue(brokerProgress is ("C", 100L, 200L, 300L, null, _));

        var initProgress = new BrokerScanProgress
        {
            DriveLetter = "D",
            Phase = BrokerScanPhase.ResolvingPaths,
            RecordsProcessed = 400,
            BytesProcessed = 500,
            TotalRecords = 600,
            TotalBytes = 700,
            Elapsed = elapsed
        };
        Assert.AreEqual("D", initProgress.DriveLetter);
        Assert.AreEqual(BrokerScanPhase.ResolvingPaths, initProgress.Phase);
        Assert.AreEqual(400L, initProgress.RecordsProcessed);
        Assert.AreEqual(500L, initProgress.BytesProcessed);
        Assert.AreEqual(600L, initProgress.TotalRecords);
        Assert.AreEqual(700L, initProgress.TotalBytes);

        var mmfProgress = new MmfWriteProgress(10, 20, 30, 40);
        var (mRec4, mBytes4, mTotRec4, mTotBytes4) = mmfProgress;
        Assert.AreEqual(10L, mRec4);
        Assert.AreEqual(20L, mBytes4);
        Assert.AreEqual(30L, mTotRec4);
        Assert.AreEqual(40L, mTotBytes4);

        Assert.IsTrue(mmfProgress is (10L, 20L, 30L, 40L));

        var (mRec5, mBytes5, mTotRec5, mTotBytes5, mPhase5) = mmfProgress;
        Assert.AreEqual(10L, mRec5);
        Assert.AreEqual(20L, mBytes5);
        Assert.AreEqual(30L, mTotRec5);
        Assert.AreEqual(40L, mTotBytes5);
        Assert.AreEqual(BrokerScanPhase.Transferring, mPhase5);

        Assert.IsTrue(mmfProgress is (10L, 20L, 30L, 40L, BrokerScanPhase.Transferring));
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    [TestMethod]
    public unsafe void ReadRecordBatches_WithProgress_ReportsNativeProgress()
    {
        MFTLibNative._getMftNativeAbiVersion = () => MFTLibNative.ExpectedMftNativeAbiVersion;
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);

        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride);
        new Span<byte>((void*)entryBuf, (int)stride).Clear();
        Unsafe.WriteUnaligned((byte*)entryBuf, 100UL);
        Unsafe.WriteUnaligned((byte*)entryBuf + 28, (ushort)1);
        Unsafe.WriteUnaligned((byte*)entryBuf + 30, (ushort)0);

        var parseResult = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = entryBuf,
            EntryStrings = IntPtr.Zero,
            EntryStringUnits = 0,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(parseResult, parsePtr, false);

        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, callback, context) =>
        {
            callback?.Invoke(MftScanPhase.Parsing, 1, 10, 15.0, context);
            return parsePtr;
        };
        MFTLibNative._freeMftResult = ptr =>
        {
            Marshal.FreeHGlobal(entryBuf);
            Marshal.FreeHGlobal(ptr);
        };

        var reported = new List<MftScanProgress>();
        var directProgress = new DirectMftProgress(reported.Add);

        using var volume = MftVolume.Open("C");
        var batches = volume.ReadRecordBatches(resolvePaths: false, 4096, directProgress).ToList();

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual(MftScanPhase.Parsing, reported[0].Phase);
        Assert.AreEqual(1L, reported[0].RecordsScanned);
        Assert.AreEqual(10L, reported[0].TotalRecords);
    }

    [TestMethod]
    public unsafe void StreamRecords_WithProgress_DeliversSamplesDuringParseExecution()
    {
        MFTLibNative._getMftNativeAbiVersion = () => MFTLibNative.ExpectedMftNativeAbiVersion;
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);

        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride);
        new Span<byte>((void*)entryBuf, (int)stride).Clear();
        Unsafe.WriteUnaligned((byte*)entryBuf, 100UL);
        Unsafe.WriteUnaligned((byte*)entryBuf + 28, (ushort)1);
        Unsafe.WriteUnaligned((byte*)entryBuf + 30, (ushort)0);

        var parseResult = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = entryBuf,
            EntryStrings = IntPtr.Zero,
            EntryStringUnits = 0,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(parseResult, parsePtr, false);

        var reported = new List<MftScanProgress>();
        var reportedDuringParse = 0;

        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, callback, context) =>
        {
            callback?.Invoke(MftScanPhase.Parsing, 1, 10, 15.0, context);
            callback?.Invoke(MftScanPhase.ResolvingPaths, 5, 10, 30.0, context);
            reportedDuringParse = reported.Count;
            return parsePtr;
        };
        MFTLibNative._freeMftResult = ptr =>
        {
            Marshal.FreeHGlobal(entryBuf);
            Marshal.FreeHGlobal(ptr);
        };

        var directProgress = new DirectMftProgress(reported.Add);

        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(null, MatchFlags.None, directProgress);

        Assert.AreEqual(2, reportedDuringParse, "Both progress samples must have arrived before parse returned");
        Assert.AreEqual(2, reported.Count);
        Assert.AreEqual(MftScanPhase.Parsing, reported[0].Phase);
        Assert.AreEqual(1L, reported[0].RecordsScanned);
        Assert.AreEqual(MftScanPhase.ResolvingPaths, reported[1].Phase);
        Assert.AreEqual(5L, reported[1].RecordsScanned);
    }

    [TestMethod]
    public unsafe void StreamRecords_ProgressCallbackThrows_SwallowsExceptionAndCompletes()
    {
        MFTLibNative._getMftNativeAbiVersion = () => MFTLibNative.ExpectedMftNativeAbiVersion;
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);

        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride);
        new Span<byte>((void*)entryBuf, (int)stride).Clear();
        Unsafe.WriteUnaligned((byte*)entryBuf, 100UL);
        Unsafe.WriteUnaligned((byte*)entryBuf + 28, (ushort)1);
        Unsafe.WriteUnaligned((byte*)entryBuf + 30, (ushort)0);

        var parseResult = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = entryBuf,
            EntryStrings = IntPtr.Zero,
            EntryStringUnits = 0,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(parseResult, parsePtr, false);

        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, callback, context) =>
        {
            callback?.Invoke(MftScanPhase.Parsing, 1, 10, 15.0, context);
            return parsePtr;
        };
        MFTLibNative._freeMftResult = ptr =>
        {
            Marshal.FreeHGlobal(entryBuf);
            Marshal.FreeHGlobal(ptr);
        };

        var throwingProgress = new DirectMftProgress(_ => throw new InvalidOperationException("Simulated UI progress failure"));

        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(null, MatchFlags.None, throwingProgress);

        Assert.AreEqual(1, result.ToArray().Length, "Parse must complete normally despite exception in progress handler");
    }

    sealed class DirectMftProgress(Action<MftScanProgress> handler) : IProgress<MftScanProgress>
    {
        public void Report(MftScanProgress value)
        {
            handler(value);
        }
    }
}
