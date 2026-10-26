using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// ReadRecordBatches drives the real native parse over a synthetic NTFS image (the volume handle
// seam returns a handle to it), in 64-record chunks.
public partial class MftVolumeTests
{
    [TestMethod]
    public void ReadRecordBatches_AllowanceLoweredBetweenChunks_LaterChunksUseTheNewCount()
    {
        WithImageVolume(volume =>
        {
            var nativeHardwareThreadCount = NativeTestHooks.NativeGetNativeHardwareThreadCount();
            var allowance = new ParseThreadAllowance((int)nativeHardwareThreadCount);
            var progress = new SynchronousProgress<MftScanProgress>(_ => allowance.Count = 1);

            var batches = volume.ReadRecordBatches(4096, progress, allowance, CancellationToken.None).ToList();

            Assert.IsNotEmpty(batches);
            var counts = ParseControlBlock.ChunkThreadCounts();
            Assert.HasCount(16, counts);
            Assert.AreEqual(nativeHardwareThreadCount, counts[0]);
            Assert.AreSequenceEqual([.. Enumerable.Repeat(1u, 15)], counts[1..]);
        });
    }

    [TestMethod]
    public void ReadRecordBatches_ParseInProgress_LeavesTheFinishedParsesCountsUntouched()
    {
        WithImageVolume(volume =>
        {
            var firstParse = volume.ReadRecordBatches(4096, null, new ParseThreadAllowance(1), CancellationToken.None)
                .ToList();
            Assert.IsNotEmpty(firstParse);
            var finishedCounts = ParseControlBlock.ChunkThreadCounts();
            Assert.HasCount(16, finishedCounts);

            uint[]? countsDuringSecondParse = null;
            var progress = new SynchronousProgress<MftScanProgress>(_ =>
                countsDuringSecondParse ??= ParseControlBlock.ChunkThreadCounts());

            var secondParse = volume.ReadRecordBatches(4096, progress, null, CancellationToken.None).ToList();

            Assert.IsNotEmpty(secondParse);
            Assert.AreSequenceEqual(finishedCounts, countsDuringSecondParse, "A running parse does not clear or extend the recording of the last finished parse.");
            Assert.HasCount(16, ParseControlBlock.ChunkThreadCounts());
        });
    }

    [TestMethod]
    public void ReadRecordBatches_TokenCancelledDuringParse_ThrowsBeforeFirstBatch()
    {
        WithImageVolume(volume =>
        {
            using var cancellation = new CancellationTokenSource();
            // Progress is reported inside the using scope.
            // ReSharper disable once AccessToDisposedClosure
            var progress = new SynchronousProgress<MftScanProgress>(_ => cancellation.Cancel());

            using var batches = volume.ReadRecordBatches(4096, progress, null, cancellation.Token)
                .GetEnumerator();
            var exception = Assert.ThrowsExactly<OperationCanceledException>(() => batches.MoveNext());

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.HasCount(1, ParseControlBlock.ChunkThreadCounts(), "The parse must stop after one chunk");
        });
    }

    [TestMethod]
    public void ReadRecordBatches_TokenCancelledBetweenBatches_ThrowsBeforeNextBatch()
    {
        WithImageVolume(volume =>
        {
            using var cancellation = new CancellationTokenSource();

            using var batches = volume.ReadRecordBatches(10, null, null, cancellation.Token).GetEnumerator();
            Assert.IsTrue(batches.MoveNext());
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(() => batches.MoveNext());
        });
    }

    static void WithImageVolume(Action<MftVolume> test)
    {
        var path = Path.GetTempFileName();
        try
        {
            SyntheticNtfsImage.Write(path, 1024);
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
            FileUtilities.Hooks._getVolumeHandle = _ => File.OpenHandle(path);
            using var volume = MftVolume.Open('C', 64);
            test(volume);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
