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

        Assert.AreEqual(500L, progress.RecordsScanned);
        Assert.AreEqual(1000L, progress.TotalRecords);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: progress.Elapsed is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, progress.Elapsed);

        var same = new MftScanProgress(500, 1000, elapsed);
        Assert.AreEqual(progress, same);
        Assert.AreEqual(progress.GetHashCode(), same.GetHashCode());

        var different = new MftScanProgress(501, 1000, elapsed);
        Assert.AreNotEqual(progress, different);
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
            callback?.Invoke(1, 10, 15.0, context);
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
            callback?.Invoke(1, 10, 15.0, context);
            callback?.Invoke(5, 10, 30.0, context);
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
        Assert.AreEqual(1L, reported[0].RecordsScanned);
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
            callback?.Invoke(1, 10, 15.0, context);
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
