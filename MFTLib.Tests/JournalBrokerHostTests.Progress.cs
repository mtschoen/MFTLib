using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A scan channel's progress frames. The progress pump is private to the host, so each case
// drives it through a scan and reads what reaches the volume pipe.
public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task ProgressPump_ThrottleClocksAreHostLocal()
    {
        var immediateFirst = new TestGate();
        var immediateSecond = new TestGate();
        var throttledFirst = new TestGate();
        var throttledSecond = new TestGate();
        var immediateClock = new FakeTimeProvider();
        var throttledClock = new FakeTimeProvider();
        var immediate = CreateProgressHost(immediateFirst, immediateSecond, immediateClock);
        var throttled = CreateProgressHost(throttledFirst, throttledSecond, throttledClock);

        await using var immediateHarness = new HostChannelHarness(immediate, new CountingBlockSectionWriter());
        await using var throttledHarness = new HostChannelHarness(throttled, new CountingBlockSectionWriter());
        try
        {
            var immediatePipe = await immediateHarness.OpenScanChannelAsync('C');
            var throttledPipe = await throttledHarness.OpenScanChannelAsync('D');
            foreach (var pipe in new[] { immediatePipe, throttledPipe })
            {
                Assert.AreEqual(BrokerFrameKind.Cursor, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
                Assert.AreEqual(5L, (await HostChannelHarness.ReadFrameAsync(pipe))?.Progress?.RecordsProcessed);
            }

            immediateClock.Advance(TimeSpan.FromMilliseconds(250));
            immediateFirst.Release();
            Assert.AreEqual(10L,
                (await HostChannelHarness.ReadFrameAsync(immediatePipe))?.Progress?.RecordsProcessed);
            throttledFirst.Release();
            await throttledSecond.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            immediateSecond.Release();
            throttledSecond.Release();

            var immediateRest = await HostChannelHarness.ReadToEndAsync(immediatePipe);
            var throttledRest = await HostChannelHarness.ReadToEndAsync(throttledPipe);
            foreach (var frames in new[] { immediateRest, throttledRest })
            {
                Assert.AreSequenceEqual([19], [.. frames
                    .Where(frame => frame.Kind == BrokerFrameKind.ScanProgress &&
                        frame.Progress!.Value.Phase == BlockScanPhase.Scanning)
                    .Select(frame => frame.Progress!.Value.RecordsProcessed)]);
                Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
            }
        }
        finally
        {
            immediateFirst.Release();
            immediateSecond.Release();
            throttledFirst.Release();
            throttledSecond.Release();
        }

        static JournalBrokerHost CreateProgressHost(TestGate first, TestGate second,
            TimeProvider timeProvider) =>
            ScanHost(scanVolume: (_, _, reporting, _, _, _) =>
            {
                reporting.Progress!.Report(BlockWriteProgress.Scanning(5, 20));
                first.WaitForRelease();
                reporting.Progress.Report(BlockWriteProgress.Scanning(10, 20));
                second.MarkEntered();
                second.WaitForRelease();
                reporting.Progress.Report(BlockWriteProgress.Scanning(19, 20));
                return [];
            }, timeProvider: timeProvider);
    }

    [TestMethod]
    public async Task ProgressPump_ThrottledLastReportIsFlushedOnCompletion()
    {
        var afterFirstReport = new TestGate();
        var host = ScanHost(scanVolume: (_, _, reporting, _, _, _) =>
        {
            reporting.Progress!.Report(BlockWriteProgress.Scanning(5, 20));
            afterFirstReport.MarkEntered();
            afterFirstReport.WaitForRelease();
            reporting.Progress.Report(BlockWriteProgress.Scanning(19, 20));
            return [[ScanRecord(5, ".", 3)]];
        }, timeProvider: new FakeTimeProvider());
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        List<BrokerFrame> rest;
        try
        {
            var pipe = await harness.OpenScanChannelAsync('C');

            // The first report is on the wire before the second is made, so the second lands
            // inside the throttle window deterministically.
            var cursor = await HostChannelHarness.ReadFrameAsync(pipe);
            var first = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.Cursor, cursor?.Kind);
            Assert.AreEqual(5L, first?.Progress?.RecordsProcessed, "The first report in the window is emitted immediately.");
            afterFirstReport.Release();
            rest = await HostChannelHarness.ReadToEndAsync(pipe);
        }
        finally
        {
            afterFirstReport.Release();
        }

        var progressFrames = rest.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
        Assert.Contains(f => f.Progress!.Value.RecordsProcessed == 19, progressFrames,
            "The newest throttled report is flushed when the progress stream completes.");
    }

    [TestMethod]
    public async Task ScanProgress_CancelledScan_EndsCleanlyWithoutErrorFrame()
    {
        var host = ScanHost(scanVolume: (_, _, _, _, _, cancellationToken) => throw new OperationCanceledException(cancellationToken));
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await ScanFramesAsync(harness);

        Assert.DoesNotContain(f => f.Kind == BrokerFrameKind.Error, frames, "Cancellation must not emit an Error frame.");
        Assert.DoesNotContain(f => f.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.ScanCompleted, frames);
    }

    [TestMethod]
    public async Task ScanProgress_IndexingSampleFollowsScanningAndPrecedesScanReady()
    {
        var clock = new FakeTimeProvider();
        var parsingReported = new TestGate();
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(scanVolume: (_, _, reporting, _, _, _) => Batches(reporting),
            timeProvider: clock);
        await using var harness = new HostChannelHarness(host, blockWriter);
        List<BrokerFrame> frames;
        try
        {
            var pipe = await harness.OpenScanChannelAsync('C');
            var cursor = await HostChannelHarness.ReadFrameAsync(pipe);
            var scanning = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.Cursor, cursor?.Kind);
            Assert.AreEqual(BlockScanPhase.Scanning, scanning?.Progress?.Phase);
            Assert.AreEqual(100L, scanning?.Progress?.RecordsProcessed);
            Assert.AreEqual(1000L, scanning?.Progress?.TotalRecords);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            parsingReported.Release();
            frames = [cursor!.Value, scanning!.Value, .. await HostChannelHarness.ReadToEndAsync(pipe)];
        }
        finally
        {
            parsingReported.Release();
        }

        IEnumerable<IReadOnlyList<MftRecord>> Batches(BlockWriteReporting reporting)
        {
            reporting.Progress!.Report(BlockWriteProgress.Scanning(100, 1000));
            parsingReported.MarkEntered();
            parsingReported.WaitForRelease();
            yield return [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")];
            yield return [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")];
        }

        var scanReadyIndex = frames.FindIndex(frame => frame.Kind == BrokerFrameKind.ScanReady);
        Assert.IsGreaterThan(0, scanReadyIndex, "ScanReady frame must be present.");
        Assert.AreEqual(BlockWriteProgress.Indexing, frames[scanReadyIndex - 1].Progress,
            "The Indexing sample is the last frame before ScanReady.");
        Assert.ContainsSingle(frame => frame.Progress?.Phase == BlockScanPhase.Indexing, frames);
    }

    [TestMethod]
    public async Task ScanProgress_PhasesArriveInOrderWithNonDecreasingCounts()
    {
        // Time is frozen. The bounded channel may coalesce intermediate reports; validate every
        // emitted frame and the mandatory Indexing frame.
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(scanVolume: (_, _, reporting, _, _, _) =>
        {
            for (var i = 1; i <= 5; i++)
            {
                reporting.Progress?.Report(BlockWriteProgress.Scanning(i * 1000, 5000));
            }

            return
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")],
                [new MftRecord(3, 0, new MftRecordFields(1, FileAttributes.Archive, 300), "r3.txt")]
            ];
        }, timeProvider: new FakeTimeProvider());
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness);
        var samples = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).Select(f => f.Progress!.Value).ToList();

        // Intermediate frames flow through a bounded(1) DropOldest channel, so the pump silently loses
        // any value it has not read before the next write. The invariants hold for whatever arrives.
        Assert.IsGreaterThanOrEqualTo(1, samples.Count, "The Indexing sample is always emitted.");
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
        Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex, "The last progress frame immediately precedes ScanReady.");
        Assert.AreEqual(BlockWriteProgress.Indexing, samples[^1]);

        var firstIndexing = samples.FindIndex(sample => sample.Phase == BlockScanPhase.Indexing);
        Assert.AreEqual(samples.Count - 1, firstIndexing, "No Scanning sample follows the Indexing sample.");
        long previousRecords = 0;
        foreach (var scanning in samples.Take(firstIndexing))
        {
            Assert.IsGreaterThanOrEqualTo(previousRecords, scanning.RecordsProcessed,
                $"Scanning RecordsProcessed must not decrease: got {scanning.RecordsProcessed}, previous {previousRecords}");
            Assert.AreEqual(5000L, scanning.TotalRecords, "Scanning TotalRecords stays 5000 throughout.");
            previousRecords = scanning.RecordsProcessed;
        }
    }

    [TestMethod]
    public async Task ScanProgress_ScanCancelledBeforeAnyProgressReported_EndsWithoutErrorFrame()
    {
        var scanStarted = new TestGate();
        var host = ScanHost(scanVolume: (_, _, _, _, _, cancellationToken) =>
        {
            scanStarted.MarkEntered();
            // Blocks without ever reporting progress, so the progress pump stays parked in its
            // first wait when the session ends.
            cancellationToken.WaitHandle.WaitOne(HangGuard);
            throw new OperationCanceledException(cancellationToken);
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipe = await harness.OpenScanChannelAsync('C');
        await scanStarted.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HangGuard, TestContext.CancellationToken);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.DoesNotContain(f => f.Kind == BrokerFrameKind.Error, frames,
            "Cancellation emits no Error frame, and the pump's own cancellation is swallowed inside the host.");
    }

    [TestMethod]
    public async Task ScanProgress_IndexingFrameImmediatelyPrecedesScanReadyAndCatchUp()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 0L),
            scanVolume: (_, _, _, _, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt")],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt")]
            ]);
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness);

        Assert.IsGreaterThanOrEqualTo(3, frames.Count, "Expected at least Cursor, ScanProgress, and ScanReady frames.");
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsGreaterThan(0, scanReadyIndex, "ScanReady frame must be present.");
        var finalProgressIndex = scanReadyIndex - 1;
        Assert.AreEqual(BrokerFrameKind.ScanProgress, frames[finalProgressIndex].Kind,
            "The Indexing frame immediately precedes ScanReady.");
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[scanReadyIndex + 1].Kind);
        Assert.AreEqual(BlockWriteProgress.Indexing, frames[finalProgressIndex].Progress);
    }

    [TestMethod]
    public async Task ScanProgress_ThrottlesScanningFramesAndEmitsIndexingImmediately()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 0L),
            scanVolume: (_, _, reporting, _, _, _) =>
            {
                var batches = new List<IReadOnlyList<MftRecord>>();
                for (var i = 0; i < 20; i++)
                {
                    reporting.Progress?.Report(BlockWriteProgress.Scanning(i, 20));
                    batches.Add([new MftRecord((ulong)i, 0, new MftRecordFields(1, FileAttributes.Archive, 100), $"r{i}.txt")]);
                }

                return batches;
            },
            timeProvider: new FakeTimeProvider());
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness);

        var samples = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).Select(f => f.Progress!.Value).ToList();
        // Frozen time admits at most the first Scanning sample: the rest are held and dropped when the
        // Indexing sample, a new phase, is written at once.
        Assert.IsLessThanOrEqualTo(1, samples.Count(sample => sample.Phase == BlockScanPhase.Scanning),
            "Frozen time admits at most one Scanning frame.");
        Assert.AreEqual(BlockWriteProgress.Indexing, samples[^1]);
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
        Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex, "The Indexing frame immediately precedes ScanReady.");
    }
}
