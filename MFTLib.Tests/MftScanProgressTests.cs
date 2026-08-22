using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        MFTLibNative.GetMftNativeAbiVersion = () => MFTLibNative.ExpectedMftNativeAbiVersion;
        FileUtilities.GetVolumeHandle = _ => new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), false);

        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)stride);
        new Span<byte>((void*)entryBuf, (int)stride).Clear();
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)entryBuf, 100UL);
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)entryBuf + 28, (ushort)1);
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)entryBuf + 30, (ushort)0);

        var parseResult = new Interop.MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = entryBuf,
            EntryStrings = IntPtr.Zero,
            EntryStringUnits = 0,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var parsePtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<Interop.MftParseResult>());
        System.Runtime.InteropServices.Marshal.StructureToPtr(parseResult, parsePtr, false);

        MFTLibNative.ParseMFTRecordsWithProgress = (_, _, _, _, callback, context) =>
        {
            callback?.Invoke(1, 10, 15.0, context);
            return parsePtr;
        };
        MFTLibNative.FreeMftResult = ptr =>
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(entryBuf);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        };

        var reported = new List<MftScanProgress>();
        var directProgress = new DirectMftProgress(reported.Add);

        using var volume = MftVolume.Open("C");
        var batches = volume.ReadRecordBatches(resolvePaths: false, batchSize: 4096, progress: directProgress).ToList();

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual(1L, reported[0].RecordsScanned);
        Assert.AreEqual(10L, reported[0].TotalRecords);
    }

    sealed class DirectMftProgress : IProgress<MftScanProgress>
    {
        readonly Action<MftScanProgress> _handler;

        public DirectMftProgress(Action<MftScanProgress> handler)
        {
            _handler = handler;
        }

        public void Report(MftScanProgress value)
        {
            _handler(value);
        }
    }
}
