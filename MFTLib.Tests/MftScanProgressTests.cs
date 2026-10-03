using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class MftScanProgressTests
{
    [TestMethod]
    public void Properties_And_Equality_WorkAsExpected()
    {
        var elapsed = TimeSpan.FromMilliseconds(1234);
        var progress = new MftScanProgress(MftScanPhase.Parsing, 500, 1000, elapsed);

        Assert.AreEqual(MftScanPhase.Parsing, progress.Phase);
        Assert.AreEqual(500L, progress.RecordsScanned);
        Assert.AreEqual(1000L, progress.TotalRecords);
        // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: progress.Elapsed is the TimeSpan literal passed to the constructor, not a clock read (schoen/aislop#51)
        Assert.AreEqual(elapsed, progress.Elapsed);

        var explicitPhase = new MftScanProgress(MftScanPhase.ResolvingPaths, 500, 1000, elapsed);
        Assert.AreEqual(MftScanPhase.ResolvingPaths, explicitPhase.Phase);
        Assert.AreNotEqual(progress, explicitPhase);

        var same = new MftScanProgress(MftScanPhase.Parsing, 500, 1000, elapsed);
        Assert.AreEqual(progress, same);
        Assert.AreEqual(progress.GetHashCode(), same.GetHashCode());

        var different = new MftScanProgress(MftScanPhase.Parsing, 501, 1000, elapsed);
        Assert.AreNotEqual(progress, different);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    [TestMethod]
    public void ReadRecordBatches_WithProgress_ReportsNativeProgress()
    {
        MftProgressParseFixture.ConfigureSingleRecordParse(callback =>
        {
            callback?.Invoke(MftScanPhase.Parsing, 1, 10, 15.0, IntPtr.Zero);
        });

        var reported = new List<MftScanProgress>();
        var directProgress = new SynchronousProgress<MftScanProgress>(reported.Add);

        using var volume = MftVolume.Open("C");
        var batches = volume.ReadRecordBatches(resolvePaths: false, 4096, directProgress, null, CancellationToken.None).ToList();

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual(MftScanPhase.Parsing, reported[0].Phase);
        Assert.AreEqual(1L, reported[0].RecordsScanned);
        Assert.AreEqual(10L, reported[0].TotalRecords);
    }

    [TestMethod]
    public void StreamRecords_WithProgress_DeliversSamplesDuringParseExecution()
    {
        var reported = new List<MftScanProgress>();
        var reportedDuringParse = 0;

        MftProgressParseFixture.ConfigureSingleRecordParse(callback =>
        {
            callback?.Invoke(MftScanPhase.Parsing, 1, 10, 15.0, IntPtr.Zero);
            callback?.Invoke(MftScanPhase.ResolvingPaths, 5, 10, 30.0, IntPtr.Zero);
            reportedDuringParse = reported.Count;
        });

        var directProgress = new SynchronousProgress<MftScanProgress>(reported.Add);

        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(null, MatchFlags.None, directProgress, null, CancellationToken.None);

        Assert.AreEqual(2, reportedDuringParse, "Both progress samples must have arrived before parse returned");
        Assert.AreEqual(2, reported.Count);
        Assert.AreEqual(MftScanPhase.Parsing, reported[0].Phase);
        Assert.AreEqual(1L, reported[0].RecordsScanned);
        Assert.AreEqual(MftScanPhase.ResolvingPaths, reported[1].Phase);
        Assert.AreEqual(5L, reported[1].RecordsScanned);
    }

    [TestMethod]
    public void StreamRecords_ProgressCallbackThrows_SwallowsExceptionAndCompletes()
    {
        MftProgressParseFixture.ConfigureSingleRecordParse(callback =>
        {
            callback?.Invoke(MftScanPhase.Parsing, 1, 10, 15.0, IntPtr.Zero);
        });

        var throwingProgress = new SynchronousProgress<MftScanProgress>(
            _ => throw new InvalidOperationException("Simulated UI progress failure"));

        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(null, MatchFlags.None, throwingProgress, null, CancellationToken.None);

        Assert.AreEqual(1, result.ToArray().Length, "Parse must complete normally despite exception in progress handler");
    }

}
