using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_CachedCursorThrowsAtStart_OneDriveDegrades_OtherDriveStreamsNormally()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursorC = new UsnJournalCursor(1UL, 999L);
        (UsnJournalEntry[], UsnJournalCursor)[] batchC = [([SampleEntry()], new UsnJournalCursor(1UL, 1010L))];
        (UsnJournalEntry[], UsnJournalCursor)[] batchD = [([SampleEntry()], new UsnJournalCursor(2UL, 210L))];

        UsnJournalCursor QueryCursor(string drive)
        {
            return drive == "C" ? freshCursorC : throw new InvalidOperationException("unexpected query");
        }

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            if (drive == "C" && since.JournalId == 7UL)
            {
                throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed");
            }

            return FakeWatch(drive == "C" ? batchC : batchD, cancellationToken);
        }

        var host = CreateHost(
            QueryCursor,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100,D:2:200");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frames = new List<BrokerFrame>();
        for (var i = 0; i < 3; i++)
        {
            frames.Add(await ReadOneFrameAsync(clientSide));
        }

        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error), "No Error frames");
        var warning = frames.Single(f => f.Kind == BrokerFrameKind.Warning);
        Assert.AreEqual("C", warning.Drive);
        StringAssert.Contains(warning.Message, "USN journal entries have been deleted");

        var batches = frames.Where(f => f.Kind == BrokerFrameKind.JournalBatch).ToList();
        Assert.AreEqual(2, batches.Count);
        Assert.IsTrue(batches.Any(b => b.Drive == "C" && b.Cursor == new UsnJournalCursor(1UL, 1010L)));
        Assert.IsTrue(batches.Any(b => b.Drive == "D" && b.Cursor == new UsnJournalCursor(2UL, 210L)));

        await cts.CancelAsync();
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
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("C", batch.Drive);

        var error = await ReadOneFrameAsync(clientSide);
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
    public async Task StartWatch_GenericStartupFailure_WithSuccessfulQueryCursor_EmitsErrorFrame_DoesNotEmitWarning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursor = new UsnJournalCursor(7UL, 200L);

        var host = CreateHost(
            _ => freshCursor,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException("Access is denied"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Access is denied", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorSameAsCurrentCursor_Fails_EmitsErrorFrame_DoesNotEmitWarning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var cursor = new UsnJournalCursor(7UL, 100L);

        var host = CreateHost(
            _ => cursor,
            (_, _, _) => [],
            (_, c) => (Array.Empty<UsnJournalEntry>(), c),
            (_, _, _) => throw new InvalidOperationException("Cannot open volume"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Cannot open volume", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorFails_RequeryThrows_EmitsErrorFrame_DoesNotEmitWarning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var host = CreateHost(
            _ => throw new InvalidOperationException("Volume disappeared"),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Volume disappeared", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorJournalIdMismatch_DegradesToWarningAndStreams()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursor = new UsnJournalCursor(8UL, 50L);
        (UsnJournalEntry[], UsnJournalCursor)[] batches = [([SampleEntry()], new UsnJournalCursor(8UL, 60L))];

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            if (since.JournalId == 7UL)
            {
                throw new InvalidOperationException("Journal ID mismatch");
            }

            return FakeWatch(batches, cancellationToken);
        }

        var host = CreateHost(
            _ => freshCursor,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var warning = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Warning, warning.Kind);
        Assert.AreEqual("C", warning.Drive);
        StringAssert.Contains(warning.Message, "Journal ID mismatch");

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("C", batch.Drive);
        Assert.AreEqual(new UsnJournalCursor(8UL, 60L), batch.Cursor);

        await cts.CancelAsync();
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
