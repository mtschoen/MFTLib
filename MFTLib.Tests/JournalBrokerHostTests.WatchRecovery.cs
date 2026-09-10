using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_StaleCachedCursor_EndsThatDrivesStreamWithARescanErrorAndTheOtherDriveKeepsStreaming()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCount = 0;
        (UsnJournalEntry[], UsnJournalCursor)[] batchD = [([SampleEntry()], new UsnJournalCursor(2UL, 210L))];

        UsnJournalCursor QueryCursor(string drive)
        {
            queryCallCount++;
            return drive == "C"
                ? new UsnJournalCursor(1UL, 999L)
                : throw new AssertFailedException($"Unexpected cursor query for drive {drive}.");
        }

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            if (drive == "C" && since.JournalId == 7UL)
            {
                throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed");
            }

            return FakeWatch(
                drive == "C"
                    ? [([SampleEntry()], new UsnJournalCursor(1UL, 1010L))]
                    : batchD,
                cancellationToken);
        }

        var host = CreateHost(
            QueryCursor,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1,D:2:200:2", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var frames = await EndWatchAndCollectFramesAsync(clientSide, cancellationSource.Token);

        var error = frames.Single(frame => frame.Kind == BrokerFrameKind.Error);
        Assert.AreEqual("C", error.Drive);
        StringAssert.Contains(error.RequireMessage(), "7:100");
        StringAssert.Contains(error.RequireMessage(), "USN journal entries have been deleted; full rescan needed");
        StringAssert.Contains(error.RequireMessage(), "rescan");
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Warning));
        Assert.AreEqual(0, queryCallCount);

        var batch = frames.Single(frame => frame.Kind == BrokerFrameKind.JournalBatch);
        Assert.AreEqual("D", batch.Drive);
        Assert.AreEqual(new UsnJournalCursor(2UL, 210L), batch.Cursor);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, frames[^1].Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_MidStreamThrows_EmitsErrorFrame_SessionContinues()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => ThrowingWatchMidStream());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("C", batch.Drive);

        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("C", error.Drive);
        Assert.AreEqual("journal wrapped mid-stream", error.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_StartupFailureThatIsNotAboutTheCursor_CarriesThePlainExceptionMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => throw new AssertFailedException("A cached cursor must not be re-queried."),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new UnauthorizedAccessException("Access is denied"));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var frame = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Access is denied", frame.Message);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_StaleCachedCursor_NeverWatchesFromTheCurrentJournalPosition()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCount = 0;
        var watchCallCount = 0;
        var host = CreateHost(
            drive =>
            {
                queryCallCount++;
                throw new AssertFailedException($"QueryCursor must not be called for cached drive {drive}.");
            },
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, since, cancellationToken) =>
            {
                watchCallCount++;
                return since == new UsnJournalCursor(7UL, 100L)
                    ? throw new InvalidOperationException("USN journal wrapped before the cached cursor")
                    : FakeWatch([([SampleEntry()], new UsnJournalCursor(8UL, 10L))], cancellationToken);
            });

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("C", error.Drive);
        StringAssert.Contains(error.RequireMessage(), "USN journal wrapped before the cached cursor");
        Assert.AreEqual(1, watchCallCount);
        Assert.AreEqual(0, queryCallCount);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursorSentinel_StartupFailure_CarriesThePlainExceptionMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        const string failureMessage = "USN journal wrapped before watching could start";
        var host = CreateHost(
            _ => new UsnJournalCursor(8UL, 50L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException(failureMessage));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:0:0:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("C", error.Drive);
        Assert.AreEqual(failureMessage, error.Message);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_JournalIdMismatchOnTheCachedCursor_IsAlsoAStaleCursorError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCount = 0;
        var host = CreateHost(
            _ =>
            {
                queryCallCount++;
                return new UsnJournalCursor(8UL, 50L);
            },
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, since, cancellationToken) => since.JournalId == 7UL
                ? throw new InvalidOperationException(
                    "Journal ID mismatch: the cached cursor refers to a journal that was recreated")
                : FakeWatch([([SampleEntry()], new UsnJournalCursor(8UL, 60L))], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var frames = await EndWatchAndCollectFramesAsync(clientSide, cancellationSource.Token);
        var error = frames.Single(frame => frame.Kind == BrokerFrameKind.Error);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("C", error.Drive);
        StringAssert.Contains(error.RequireMessage(), "7:100");
        StringAssert.Contains(error.RequireMessage(), "Journal ID mismatch");
        StringAssert.Contains(error.RequireMessage(), "rescan");
        Assert.AreEqual(0, queryCallCount);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Warning));
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, frames[^1].Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_EmitsFinalFrameImmediatelyBeforeScanReady()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "r1.txt", null)],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "r2.txt", null)]
            ],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-progress-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var blockWriter = CreateSectionWriter();
        await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsTrue(frames.Count >= 3, "Expected at least Cursor, ScanProgress, and ScanReady frames");
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);

        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present");
        var finalProgressIndex = scanReadyIndex - 1;
        Assert.AreEqual(BrokerFrameKind.ScanProgress, frames[finalProgressIndex].Kind,
            "Final progress frame must immediately precede ScanReady");
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[scanReadyIndex + 1].Kind);

        var progress = frames[finalProgressIndex].Progress;
        Assert.IsNotNull(progress);
        Assert.AreEqual("C", progress.Value.DriveLetter);
        Assert.AreEqual(3L, progress.Value.RecordsProcessed);
        Assert.AreEqual(3L, progress.Value.TotalRecords);
        Assert.IsTrue(progress.Value.BytesProcessed > 0);
        Assert.AreEqual(progress.Value.BytesProcessed, progress.Value.TotalBytes);
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_ThrottlesNonFinalFrames()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.FromMinutes(10);

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, progress, _) =>
                {
                    var batches = new List<IReadOnlyList<MftRecord>>();
                    for (var i = 0; i < 20; i++)
                    {
                        progress?.Report(new BlockWriteProgress(i, i * 100, 20, 2000));
                        batches.Add([new MftRecord((ulong)i, 0, new MftRecordFields(1, FileAttributes.Archive, 100), $"r{i}.txt", null)]);
                    }

                    return batches;
                },
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-throttle-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            using var blockWriter = CreateSectionWriter();
            await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
            // With a 10-minute throttle interval, the rapid burst of 20 reports emits at most 1 initial non-final
            // frame, plus at most 1 flush of the newest throttled report when the progress stream completes,
            // plus 1 final synthetic frame before ScanReady (at most 3 total progress frames).
            Assert.IsTrue(progressFrames.Count >= 1, "At least one progress frame must be emitted");
            Assert.IsTrue(progressFrames.Count <= 3,
                $"Expected at most 3 progress frames due to throttling, but got {progressFrames.Count}");
            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
            Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex,
                "Final progress frame must immediately precede ScanReady");
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }
}
