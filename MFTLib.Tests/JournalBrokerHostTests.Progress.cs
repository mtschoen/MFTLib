using System.Buffers;
using System.Threading.Channels;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task ProgressPump_ThrottledLastReportIsFlushedOnCompletion()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.FromMinutes(10);

            var channel = Channel.CreateBounded<BrokerScanProgress>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
            using var writeLock = new SemaphoreSlim(1, 1);
            using var stream = new MemoryStream();

            var pump = JournalBrokerHost.RunProgressPumpAsync(
                stream, channel.Reader, writeLock, CancellationToken.None);

            Assert.IsTrue(channel.Writer.TryWrite(
                new BrokerScanProgress("C", 5, 500, 20, 2000, TimeSpan.FromSeconds(1))));

            // Wait until the first report is on the wire before writing the second, so the
            // second deterministically lands inside the throttle window instead of racing
            // the capacity-1 channel. Condition poll with a bounded number of attempts.
            var firstEmitted = false;
            for (var attempt = 0; attempt < 500 && !firstEmitted; attempt++)
            {
                await writeLock.WaitAsync();
                firstEmitted = stream.Length > 0;
                writeLock.Release();
                if (!firstEmitted)
                {
                    await Task.Delay(10);
                }
            }

            Assert.IsTrue(firstEmitted, "The first report must be emitted immediately");

            Assert.IsTrue(channel.Writer.TryWrite(
                new BrokerScanProgress("C", 19, 1900, 20, 2000, TimeSpan.FromSeconds(2))));
            channel.Writer.TryComplete();
            await pump;

            using var replay = new MemoryStream(stream.ToArray());
            var frames = ReadAllFrames(replay);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
            Assert.IsTrue(progressFrames.Any(f => f.Progress!.Value.RecordsProcessed == 5),
                "The first report in the window must be emitted immediately");
            Assert.IsTrue(progressFrames.Any(f => f.Progress!.Value.RecordsProcessed == 19),
                "The newest throttled report must be flushed when the progress stream completes");
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_CancelledScan_EndsCleanlyWithoutErrorFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, ct) => throw new OperationCanceledException(ct),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-cancel-C");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var blockWriter = CreateSectionWriter();
        await host.ServeAsync(serverSide, blockWriter, true, cts.Token);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error),
            "Cancellation must not emit an Error frame");
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_MonotonicAcrossParseAndWritePhases()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            // Zero, not a small interval: a non-zero throttle makes emission depend on
            // real elapsed time between reports, which is what made this test flake on
            // CI. With Zero the pump emits every value it manages to read.
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, progress, _) =>
                {
                    for (var i = 1; i <= 5; i++)
                    {
                        progress?.Report(new BlockWriteProgress(
                            i * 1000,
                            0,
                            5000,
                            null,
                            BrokerScanPhase.Parsing));
                    }

                    return
                    [
                        [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt", null)],
                        [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt", null)],
                        [new MftRecord(3, 0, new MftRecordFields(1, FileAttributes.Archive, 300), "r3.txt", null)]
                    ];
                },
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-monotonic-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            using var blockWriter = CreateSectionWriter();
            await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            // Only the final frame is guaranteed. Intermediate frames flow through a
            // bounded(1)/DropOldest channel, so the pump silently loses any value it has
            // not read before the next write - how many survive depends on thread
            // scheduling, not on behaviour under test. Asserting a count above one is
            // what made this flaky. The invariants below hold for whatever does arrive.
            Assert.IsTrue(progressFrames.Count >= 1, "The final progress frame is always emitted");

            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
            Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex,
                "Final progress frame must immediately precede ScanReady");

            // Elapsed is not asserted here. The host fills it from ScanProgressState's real
            // Stopwatch, so comparing successive values is a wall-clock assertion, and the
            // suppression that used to sit here claimed the opposite. Testing that elapsed
            // propagates needs an injected clock, which this test does not have.
            long previousParsingRecords = 0;
            long previousTransferringRecords = 0;
            foreach (var frame in progressFrames)
            {
                Assert.IsNotNull(frame.Progress);
                var p = frame.Progress.Value;
                Assert.AreEqual("C", p.DriveLetter);
                if (p.Phase == BrokerScanPhase.Parsing)
                {
                    Assert.IsTrue(p.RecordsProcessed >= previousParsingRecords,
                        $"Parsing RecordsProcessed must be non-decreasing: got {p.RecordsProcessed}, previous was {previousParsingRecords}");
                    Assert.AreEqual(5000L, p.TotalRecords, "Parsing TotalRecords must stay 5000 throughout");
                    previousParsingRecords = p.RecordsProcessed;
                }
                else if (p.Phase == BrokerScanPhase.Transferring)
                {
                    Assert.IsTrue(p.RecordsProcessed >= previousTransferringRecords,
                        $"Transferring RecordsProcessed must be non-decreasing: got {p.RecordsProcessed}");
                    previousTransferringRecords = p.RecordsProcessed;
                }
            }

            var finalProgress = progressFrames.Last().Progress!.Value;
            Assert.AreEqual(5000L, finalProgress.RecordsProcessed);
            Assert.AreEqual(5000L, finalProgress.TotalRecords);
            Assert.IsTrue(finalProgress.BytesProcessed > 0);
            Assert.AreEqual(finalProgress.BytesProcessed, finalProgress.TotalBytes);
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_WritePhaseByteProgressFlowsThroughBeforeCompletion()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            // The fake scan source reports no progress of its own (mirroring the
            // production ScanDriveBatches parse-phase adapter, which always reports
            // BytesProcessed = 0), so the only way a ScanProgress frame with
            // BytesProcessed > 0 can reach the wire before the guaranteed completion
            // frame is if ExecuteDriveScanAsync wires the same progress reporter into
            // the streaming writer's Write call, letting the write phase's own
            // per-batch byte progress (RecordingBlockSectionWriter) flow through.
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, _, _) =>
                [
                    [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt", null)],
                    [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt", null)]
                ],
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-writebytes-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            using var blockWriter = CreateSectionWriter();
            await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present");
            var finalProgressIndex = scanReadyIndex - 1;

            var beforeCompletion = frames.Take(finalProgressIndex)
                .Where(f => f.Kind == BrokerFrameKind.ScanProgress)
                .ToList();
            Assert.IsTrue(beforeCompletion.Count > 0,
                "Expected a write-phase ScanProgress frame on the wire before the guaranteed completion frame");
            Assert.IsTrue(beforeCompletion.All(f => f.Progress!.Value.BytesProcessed > 0),
                "Write-phase progress frames must report nonzero bytes processed");
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ProgressPump_ScanProgress_EmitsParsingAndTransferring()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;
            using var stream = new MemoryStream();
            using var writeLock = new SemaphoreSlim(1, 1);
            var channel = Channel.CreateUnbounded<BrokerScanProgress>();

            channel.Writer.TryWrite(new BrokerScanProgress
            {
                DriveLetter = "C",
                Phase = BrokerScanPhase.Parsing,
                RecordsProcessed = 500,
                BytesProcessed = 0,
                TotalRecords = 1000,
                TotalBytes = null,
                Elapsed = TimeSpan.FromMilliseconds(10)
            });
            channel.Writer.TryWrite(new BrokerScanProgress
            {
                DriveLetter = "C",
                Phase = BrokerScanPhase.Transferring,
                RecordsProcessed = 200,
                BytesProcessed = 1024,
                TotalRecords = 200,
                TotalBytes = 1024,
                Elapsed = TimeSpan.FromMilliseconds(30)
            });
            channel.Writer.TryComplete();

            await JournalBrokerHost.RunProgressPumpAsync(stream, channel.Reader, writeLock, CancellationToken.None);

            using var replay = new MemoryStream(stream.ToArray());
            var frames = ReadAllFrames(replay);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            Assert.AreEqual(2, progressFrames.Count);
            Assert.AreEqual(BrokerScanPhase.Parsing, progressFrames[0].Progress!.Value.Phase);
            Assert.AreEqual(500L, progressFrames[0].Progress!.Value.RecordsProcessed);
            Assert.AreEqual(1000L, progressFrames[0].Progress!.Value.TotalRecords);

            Assert.AreEqual(BrokerScanPhase.Transferring, progressFrames[1].Progress!.Value.Phase);
            Assert.AreEqual(200L, progressFrames[1].Progress!.Value.RecordsProcessed);
            Assert.AreEqual(200L, progressFrames[1].Progress!.Value.TotalRecords);
            Assert.AreEqual(1024L, progressFrames[1].Progress!.Value.BytesProcessed);
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_PumpCancelledBeforeAnyProgressReported_SwallowsCancellationInPump()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource();
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, ct) =>
            {
                scanStarted.TrySetResult();
                // Block without ever reporting progress, so the progress channel stays
                // empty and RunProgressPumpAsync stays parked in its first
                // reader.WaitToReadAsync call when the caller cancels - the condition
                // needed to hit the pump's own catch (OperationCanceledException)
                // rather than observing the channel complete normally afterward.
                ct.WaitHandle.WaitOne();
                throw new OperationCanceledException(ct);
            },
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-pump-cancel-C");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var blockWriter = CreateSectionWriter();
        var serveTask = host.ServeAsync(serverSide, blockWriter, true, cts.Token);

        await scanStarted.Task;
        await cts.CancelAsync();

        var finished = await Task.WhenAny(serveTask, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.AreSame(serveTask, finished, "ServeAsync must return promptly after cancellation, not hang");
        await serveTask;
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error),
            "Cancellation must not emit an Error frame, and the progress pump's own cancellation must be swallowed internally");
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_MaxRecordsProcessedExceedsReportedTotals_ClampsFinalCounts()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, progress, _) =>
            {
                // Report more records processed than the (deliberately understated)
                // TotalRecords estimate, so EmitScanCompletionFramesAsync's clamp has to
                // bump both finalRecords and finalTotalRecords up to maxRecordsProcessed
                // instead of trusting the last reported total.
                progress?.Report(new BlockWriteProgress(10, 500, 5, 1000, BrokerScanPhase.Parsing));
                return
                [
                    [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt", null)]
                ];
            },
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-clamp-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var blockWriter = CreateSectionWriter();
        await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present");
        var finalProgress = frames[scanReadyIndex - 1].Progress;
        Assert.IsNotNull(finalProgress);
        Assert.AreEqual(10L, finalProgress.Value.RecordsProcessed,
            "finalRecords must be clamped up to maxRecordsProcessed when the reported total undercounts it");
        Assert.AreEqual(10L, finalProgress.Value.TotalRecords,
            "finalTotalRecords must be clamped up to the clamped finalRecords, not left at the stale reported total");
    }
}
