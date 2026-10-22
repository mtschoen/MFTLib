using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public partial class MftVolumeTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
    }

    [TestMethod]
    public void ReadRecordBatches_ProgressCallbackElapsedMsIsNaN_SwallowsExceptionAndStillCompletes()
    {
        // The native progress adapter in MftVolume.ReadRecordBatches wraps sample
        // construction and the progress.Report call in a bare try/catch specifically
        // because it runs inside a delegate invoked from unmanaged code: an exception
        // must never cross that boundary. Feeding an elapsedMs of NaN makes
        // TimeSpan.FromMilliseconds throw while constructing the MftScanProgress,
        // exercising that catch. The malformed sample must be dropped (never reach
        // progress.Report), while the parse itself still succeeds normally.
        MftProgressParseFixture.ConfigureSingleRecordParse(callback =>
        {
            callback?.Invoke(1, 10, double.NaN);
        });

        var reported = new List<MftScanProgress>();
        var directProgress = new SynchronousProgress<MftScanProgress>(reported.Add);

        using var volume = MftVolume.Open('C');
        var batches = volume.ReadRecordBatches(4096, directProgress, null, CancellationToken.None).ToList();

        Assert.HasCount(1, batches, "The parse itself must still succeed despite the malformed progress sample");
        Assert.IsEmpty(reported, "The NaN-elapsed sample must be dropped, not reported");
    }

    [TestMethod]
    public void ReadRecordBatches_ProgressReportedLive_AllSamplesArriveBeforeNativeCallReturns()
    {
        // MftVolume.ReadRecordBatches must call progress.Report(sample) directly from the
        // native callback, not buffer samples into a queue and drain them only after
        // MFTLibNative.Hooks._parseMftRecordsWithProgress returns. The fake native function
        // below invokes the callback three times and then, still inside that same
        // delegate invocation (i.e. before the "native call" has returned to
        // ReadRecordBatches), snapshots how many samples the consumer has received. With
        // live delivery that snapshot must already be 3.
        var receivedCount = 0;
        var receivedCountAtReturn = -1;

        MftProgressParseFixture.ConfigureSingleRecordParse(callback =>
        {
            callback?.Invoke(1, 3, 10);
            callback?.Invoke(2, 3, 20);
            callback?.Invoke(3, 3, 30);
            receivedCountAtReturn = receivedCount;
        });

        var directProgress = new SynchronousProgress<MftScanProgress>(_ => receivedCount++);

        using var volume = MftVolume.Open('C');
        var batches = volume.ReadRecordBatches(4096, directProgress, null, CancellationToken.None).ToList();

        Assert.HasCount(1, batches);
        Assert.AreEqual(3, receivedCountAtReturn,
            "All three progress samples must reach the consumer before the native call returns");
        Assert.AreEqual(3, receivedCount);
    }

    [TestMethod]
    public void ReadRecordBatches_ProgressConsumerThrows_DoesNotAbortParseAndLaterSamplesStillArrive()
    {
        // The never-throw-across-the-unmanaged-boundary guarantee must hold for a
        // consumer that throws from Report, not just for a malformed sample: the parse
        // must still complete, and later samples must still reach the consumer.
        MftProgressParseFixture.ConfigureSingleRecordParse(callback =>
        {
            callback?.Invoke(1, 2, 10);
            callback?.Invoke(2, 2, 20);
        });

        var reported = new List<MftScanProgress>();
        var throwOnFirst = true;
        var directProgress = new SynchronousProgress<MftScanProgress>(sample =>
        {
            if (throwOnFirst)
            {
                throwOnFirst = false;
                throw new InvalidOperationException("Consumer failure must not cross the unmanaged boundary");
            }

            reported.Add(sample);
        });

        using var volume = MftVolume.Open('C');
        var batches = volume.ReadRecordBatches(4096, directProgress, null, CancellationToken.None).ToList();

        Assert.HasCount(1, batches, "The parse itself must still succeed despite the throwing consumer");
        Assert.HasCount(1, reported, "The sample after the throwing report must still arrive");
        Assert.AreEqual(2, reported[0].RecordsScanned);
    }
}
