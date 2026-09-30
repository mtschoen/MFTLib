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
            var nativeHardwareThreadCount = MFTLibNative.NativeGetNativeHardwareThreadCount();
            var allowance = new ParseThreadAllowance((int)nativeHardwareThreadCount);
            var progress = new SynchronousProgress<MftScanProgress>(_ => allowance.Count = 1);

            var batches = volume.ReadRecordBatches(false, 4096, progress, allowance, CancellationToken.None).ToList();

            Assert.IsTrue(batches.Count > 0);
            var counts = ParseControlBlock.ChunkThreadCounts();
            Assert.AreEqual(16, counts.Length);
            Assert.AreEqual(nativeHardwareThreadCount, counts[0]);
            CollectionAssert.AreEqual(Enumerable.Repeat(1u, 15).ToArray(), counts[1..]);
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

            using var batches = volume.ReadRecordBatches(false, 4096, progress, null, cancellation.Token)
                .GetEnumerator();
            // The assertion runs the lambda synchronously.
            // ReSharper disable once AccessToDisposedClosure
            var exception = Assert.ThrowsException<OperationCanceledException>(() => batches.MoveNext());

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.AreEqual(1, ParseControlBlock.ChunkThreadCounts().Length, "The parse must stop after one chunk");
        });
    }

    [TestMethod]
    public void ReadRecordBatches_TokenCancelledBetweenBatches_ThrowsBeforeNextBatch()
    {
        WithImageVolume(volume =>
        {
            using var cancellation = new CancellationTokenSource();

            using var batches = volume.ReadRecordBatches(false, 10, null, null, cancellation.Token).GetEnumerator();
            Assert.IsTrue(batches.MoveNext());
            cancellation.Cancel();

            // The assertion runs the lambda synchronously.
            // ReSharper disable once AccessToDisposedClosure
            Assert.ThrowsException<OperationCanceledException>(() => batches.MoveNext());
        });
    }

    static void WithImageVolume(Action<MftVolume> test)
    {
        var path = Path.GetTempFileName();
        try
        {
            SyntheticNtfsImage.Write(path, 1024);
            MFTLibNative.NativeSetVolumeRecordSizeOverride(1024);
            FileUtilities._getVolumeHandle = _ => File.OpenHandle(path);
            using (var volume = MftVolume.Open("C", 64))
            {
                test(volume);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
