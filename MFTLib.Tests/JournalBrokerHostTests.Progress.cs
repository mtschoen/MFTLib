using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A scan channel's progress frames. The progress pump is private to the host, so each case
// drives it through a scan and reads what reaches the drive pipe.
public partial class JournalBrokerHostTests
{
    static async Task WithScanProgressThrottleAsync(TimeSpan interval, Func<Task> test)
    {
        var original = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = interval;
            await test();
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = original;
        }
    }

    [TestMethod]
    public Task ProgressPump_ThrottledLastReportIsFlushedOnCompletion()
    {
        return WithScanProgressThrottleAsync(TimeSpan.FromMinutes(10), async () =>
        {
            var afterFirstReport = new TestGate();
            var host = ScanHost(scanDrive: (_, _, _, progress, _, _) =>
            {
                progress!.Report(new BlockWriteProgress(5, 0, 20, null, BrokerScanPhase.Parsing));
                afterFirstReport.MarkEntered();
                afterFirstReport.WaitForRelease();
                progress.Report(new BlockWriteProgress(19, 0, 20, null, BrokerScanPhase.Parsing));
                return [[ScanRecord(5, ".", 3)]];
            });
            await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
            var pipe = await harness.OpenScanChannelAsync('C');

            // The first report is on the wire before the second is made, so the second lands
            // inside the throttle window deterministically.
            var cursor = await HostChannelHarness.ReadFrameAsync(pipe);
            var first = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.Cursor, cursor?.Kind);
            Assert.AreEqual(5L, first?.Progress?.RecordsProcessed, "The first report in the window is emitted immediately.");
            afterFirstReport.Release();
            var rest = await HostChannelHarness.ReadToEndAsync(pipe);

            var progressFrames = rest.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
            Assert.IsTrue(progressFrames.Any(f => f.Progress!.Value.RecordsProcessed == 19),
                "The newest throttled report is flushed when the progress stream completes.");
        });
    }

    [TestMethod]
    public async Task ScanProgress_CancelledScan_EndsCleanlyWithoutErrorFrame()
    {
        var host = ScanHost(scanDrive: (_, _, _, _, _, cancellationToken) => throw new OperationCanceledException(cancellationToken));
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await ScanFramesAsync(harness);

        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error), "Cancellation must not emit an Error frame.");
        Assert.IsFalse(frames.Any(f => f.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.ScanCompleted));
    }

    [TestMethod]
    public Task ScanProgress_WritePhase_PassesProgressToStreamingWriterAndReportsBytes()
    {
        return WithScanProgressThrottleAsync(TimeSpan.Zero, async () =>
        {
            using var blockWriter = new RecordingBlockSectionWriter();
            var host = ScanHost(scanDrive: (_, _, _, progress, _, _) =>
            {
                progress?.Report(new BlockWriteProgress(100, 0, 1000, null, BrokerScanPhase.Parsing));
                return
                [
                    [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")],
                    [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")]
                ];
            });
            await using var harness = new HostChannelHarness(host, blockWriter);

            var frames = await ScanFramesAsync(harness);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            Assert.IsTrue(progressFrames.Count >= 2, "Expected intermediate parse and write progress frames.");
            Assert.IsTrue(progressFrames.Any(f => f.Progress?.BytesProcessed > 0),
                "At least one progress frame must report BytesProcessed > 0 from the write phase.");
            var finalProgress = progressFrames[^1].Progress!.Value;
            Assert.AreEqual(1000L, finalProgress.TotalRecords);
            Assert.IsTrue(finalProgress.BytesProcessed > 0);
            Assert.AreEqual(finalProgress.BytesProcessed, finalProgress.TotalBytes);
        });
    }

    [TestMethod]
    public Task ScanProgress_MonotonicAcrossParseAndWritePhases()
    {
        // Zero, not a small interval: a non-zero throttle makes emission depend on real elapsed
        // time between reports. With Zero the pump emits every value it manages to read.
        return WithScanProgressThrottleAsync(TimeSpan.Zero, async () =>
        {
            using var blockWriter = new RecordingBlockSectionWriter();
            var host = ScanHost(scanDrive: (_, _, _, progress, _, _) =>
            {
                for (var i = 1; i <= 5; i++)
                {
                    progress?.Report(new BlockWriteProgress(i * 1000, 0, 5000, null, BrokerScanPhase.Parsing));
                }

                return
                [
                    [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")],
                    [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")],
                    [new MftRecord(3, 0, new MftRecordFields(1, FileAttributes.Archive, 300), "r3.txt")]
                ];
            });
            await using var harness = new HostChannelHarness(host, blockWriter);

            var frames = await ScanFramesAsync(harness);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            // Only the final frame is guaranteed. Intermediate frames flow through a bounded(1)
            // DropOldest channel, so the pump silently loses any value it has not read before the
            // next write. The invariants below hold for whatever does arrive.
            Assert.IsTrue(progressFrames.Count >= 1, "The final progress frame is always emitted.");
            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
            Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex, "The final progress frame immediately precedes ScanReady.");

            // Elapsed is not asserted: the host fills it from a real Stopwatch.
            long previousParsingRecords = 0;
            long previousTransferringRecords = 0;
            foreach (var progress in progressFrames.Select(f => f.Progress!.Value))
            {
                Assert.AreEqual(string.Empty, progress.DriveLetter, "The drive belongs to the channel, not the frame.");
                if (progress.Phase == BrokerScanPhase.Parsing)
                {
                    Assert.IsTrue(progress.RecordsProcessed >= previousParsingRecords,
                        $"Parsing RecordsProcessed must not decrease: got {progress.RecordsProcessed}, previous {previousParsingRecords}");
                    Assert.AreEqual(5000L, progress.TotalRecords, "Parsing TotalRecords stays 5000 throughout.");
                    previousParsingRecords = progress.RecordsProcessed;
                }
                else
                {
                    Assert.IsTrue(progress.RecordsProcessed >= previousTransferringRecords,
                        $"Transferring RecordsProcessed must not decrease: got {progress.RecordsProcessed}");
                    previousTransferringRecords = progress.RecordsProcessed;
                }
            }

            var finalProgress = progressFrames[^1].Progress!.Value;
            Assert.AreEqual(5000L, finalProgress.RecordsProcessed);
            Assert.AreEqual(5000L, finalProgress.TotalRecords);
            Assert.IsTrue(finalProgress.BytesProcessed > 0);
            Assert.AreEqual(finalProgress.BytesProcessed, finalProgress.TotalBytes);
        });
    }

    [TestMethod]
    public Task ScanProgress_WritePhaseByteProgressFlowsThroughBeforeCompletion()
    {
        return WithScanProgressThrottleAsync(TimeSpan.Zero, async () =>
        {
            // The scan source reports no progress of its own (the production parse-phase adapter
            // reports BytesProcessed = 0), so a frame with BytesProcessed > 0 can reach the wire
            // before the completion frame only if the host wires the same reporter into the
            // section writer, letting the write phase's per-batch byte progress flow through.
            using var blockWriter = new RecordingBlockSectionWriter();
            var host = ScanHost(scanDrive: (_, _, _, _, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")]
            ]);
            await using var harness = new HostChannelHarness(host, blockWriter);

            var frames = await ScanFramesAsync(harness);

            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present.");
            var beforeCompletion = frames.Take(scanReadyIndex - 1).Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
            Assert.IsTrue(beforeCompletion.Count > 0,
                "Expected a write-phase ScanProgress frame before the completion frame.");
            Assert.IsTrue(beforeCompletion.All(f => f.Progress!.Value.BytesProcessed > 0),
                "Write-phase progress frames report nonzero bytes processed.");
        });
    }

    [TestMethod]
    public async Task ScanProgress_ScanCancelledBeforeAnyProgressReported_EndsWithoutErrorFrame()
    {
        var scanStarted = new TestGate();
        var host = ScanHost(scanDrive: (_, _, _, _, _, cancellationToken) =>
        {
            scanStarted.MarkEntered();
            // Blocks without ever reporting progress, so the progress pump stays parked in its
            // first wait when the session ends.
            cancellationToken.WaitHandle.WaitOne(HostChannelHarness.HangGuard);
            throw new OperationCanceledException(cancellationToken);
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipe = await harness.OpenScanChannelAsync('C');
        await scanStarted.Entered.WaitAsync(HostChannelHarness.HangGuard);

        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error),
            "Cancellation emits no Error frame, and the pump's own cancellation is swallowed inside the host.");
    }

    [TestMethod]
    public async Task ScanProgress_MaximumRecordsProcessedExceedsReportedTotals_ClampsFinalCounts()
    {
        // More records processed than the (deliberately understated) TotalRecords, so the final
        // frame has to bump both counts up to the processed maximum instead of trusting the
        // last reported total.
        var host = ScanHost(scanDrive: (_, _, _, progress, _, _) =>
        {
            progress!.Report(new BlockWriteProgress(10, 500, 5, 1000, BrokerScanPhase.Parsing));
            return [[new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")]];
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await ScanFramesAsync(harness);

        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present.");
        var finalProgress = frames[scanReadyIndex - 1].Progress;
        Assert.IsNotNull(finalProgress);
        Assert.AreEqual(10L, finalProgress.Value.RecordsProcessed,
            "The final count is clamped up to the processed maximum when the reported total undercounts it.");
        Assert.AreEqual(10L, finalProgress.Value.TotalRecords,
            "The final total is clamped up to the clamped count, not left at the stale reported total.");
    }

    [TestMethod]
    public async Task ScanProgress_FinalFrameImmediatelyPrecedesScanReadyAndCatchUp()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 0L),
            scanDrive: (_, _, _, _, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")]
            ]);
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness, 'C', "mftlib-progress-C");

        Assert.IsTrue(frames.Count >= 3, "Expected at least Cursor, ScanProgress, and ScanReady frames.");
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present.");
        var finalProgressIndex = scanReadyIndex - 1;
        Assert.AreEqual(BrokerFrameKind.ScanProgress, frames[finalProgressIndex].Kind,
            "The final progress frame immediately precedes ScanReady.");
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[scanReadyIndex + 1].Kind);
        var progress = frames[finalProgressIndex].Progress;
        Assert.IsNotNull(progress);
        Assert.AreEqual(string.Empty, progress.Value.DriveLetter, "The drive belongs to the channel, not the frame.");
        Assert.AreEqual(3L, progress.Value.RecordsProcessed);
        Assert.AreEqual(3L, progress.Value.TotalRecords);
        Assert.IsTrue(progress.Value.BytesProcessed > 0);
        Assert.AreEqual(progress.Value.BytesProcessed, progress.Value.TotalBytes);
    }

    [TestMethod]
    public Task ScanProgress_ThrottlesNonFinalFrames()
    {
        return WithScanProgressThrottleAsync(TimeSpan.FromMinutes(10), async () =>
        {
            using var blockWriter = new RecordingBlockSectionWriter();
            var host = ScanHost(
                queryCursor: _ => new UsnJournalCursor(7UL, 0L),
                scanDrive: (_, _, _, progress, _, _) =>
                {
                    var batches = new List<IReadOnlyList<MftRecord>>();
                    for (var i = 0; i < 20; i++)
                    {
                        progress?.Report(new BlockWriteProgress(i, i * 100, 20, 2000));
                        batches.Add([new MftRecord((ulong)i, 0, new MftRecordFields(1, FileAttributes.Archive, 100), $"r{i}.txt")]);
                    }

                    return batches;
                });
            await using var harness = new HostChannelHarness(host, blockWriter);

            var frames = await ScanFramesAsync(harness, 'C', "mftlib-throttle-C");

            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
            // With a 10-minute interval the burst of 20 reports emits at most one initial frame,
            // one flush of the newest throttled report, and the final frame before ScanReady.
            Assert.IsTrue(progressFrames.Count >= 1, "At least one progress frame must be emitted.");
            Assert.IsTrue(progressFrames.Count <= 3,
                $"Expected at most 3 progress frames due to throttling, but got {progressFrames.Count}");
            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
            Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex, "The final progress frame immediately precedes ScanReady.");
        });
    }
}
