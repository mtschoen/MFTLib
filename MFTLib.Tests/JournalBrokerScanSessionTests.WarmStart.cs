using System.Buffers;
using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task Rescan_WhileStartWatchInFlight_ThrowsWithoutTouchingPipe()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var startWatchTask = session.StartWatchAsync();
        await gate.Entered; // StartWatch write is blocked mid-flight; State is still Parked

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RescanAsync(CreateOptions(session.Profile)));
        StringAssert.Contains(exception.Message, "Another session operation is in progress");
        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        gate.Release();
        var watchFrame = await watchFrameTask;
        await startWatchTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        Assert.AreEqual(JournalBrokerSessionState.Watching, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_ConcurrentDoubleCall_ExactlyOneProceeds()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.ArmAndScan, 2);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var firstRescanBrokerTask = RespondToArmAndScanAsync(serverSide, "C");
        var firstRescanTask = session.RescanAsync(CreateOptions(session.Profile));
        await gate.Entered; // first rescan's ArmAndScan write is blocked mid-flight

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RescanAsync(CreateOptions(session.Profile)));
        StringAssert.Contains(exception.Message, "Another session operation is in progress");

        gate.Release();
        await firstRescanBrokerTask;
        await firstRescanTask;

        // Flag cleared once the first rescan finished: a subsequent rescan succeeds.
        var secondRescanBrokerTask = RespondToArmAndScanAsync(serverSide, "C");
        await session.RescanAsync(CreateOptions(session.Profile));
        await secondRescanBrokerTask;

        await session.DisposeAsync();
    }

    // ── Warm start (StartFromCursorsAsync) ────────────────────────────────────

    [TestMethod]
    public async Task StartFromCursors_ParksWithoutScanning_LatestScanNull()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var connectCount = 0;

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C:\\"] = new(7UL, 200L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ =>
            {
                connectCount++;
                return Task.FromResult(client);
            },
            cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.IsNull(session.LatestScan);
        Assert.AreEqual(1, connectCount);
        CollectionAssert.AreEqual(BareDriveC, session.Drives.ToArray());
        Assert.IsFalse(session.IsFaulted);

        // No ArmAndScan was sent: the first frame the broker sees is the StartWatch.
        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;
        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartFromCursors_WatchesFromSuppliedCursors_EventsFlow()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var suppliedCursor = new UsnJournalCursor(7UL, 200L);
        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = suppliedCursor };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        // The watch spec resumes from the supplied cursor, not a sentinel.
        StringAssert.Contains(watchFrame.DrivesSpec, "C:7:200");

        var liveCursor = new UsnJournalCursor(7UL, 260L);
        var entry = JournalEntryFactory.Create(1, 210, "warm.txt");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", liveCursor, [entry]);
        BrokerProtocol.WriteEndWatchAck(response);
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in session.WatchDriveAsync("C", timeout.Token))
        {
            received.Add(batch);
        }

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(liveCursor, received[0].Cursor);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartFromCursors_SentinelCursor_WatchesFromCurrent()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        // A (0,0) cursor is the "watch from current" sentinel the host resolves.
        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(0UL, 0L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        StringAssert.Contains(watchFrame.DrivesSpec, "C:0:0");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartFromCursors_RescanAfterWarmStart_PopulatesLatestScanAndRewatchesFromScan()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = new JournalBrokerClient(clientSide, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));
        var keepFileNames = new[] { "note.txt" };

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 200L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.DirectoryIndex,
            CancellationToken.None);

        Assert.IsNull(session.LatestScan);

        // Rescan arms and scans on the same broker with the warm-start profile; the keep-file
        // names come from the rescan options alone, since a warm start carries none. The
        // drives default to the warm-start volumes.
        var advancedCursor = new UsnJournalCursor(9UL, 400L);
        var rescanFrameTask = ReadOneFrameAsync(serverSide);
        var rescanTask = Task.Run(async () =>
        {
            var frame = await rescanFrameTask;
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(9UL, 350L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 1, 1, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", advancedCursor, Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            return frame;
        });

        await session.RescanAsync(CreateOptions(session.Profile, keepFileNames));
        var rescanFrame = await rescanTask;

        StringAssert.Contains(rescanFrame.DrivesSpec, $"C:0:0:mftlib-null-C:{(int)BrokerScanProfile.DirectoryIndex}");
        CollectionAssert.AreEqual(keepFileNames, rescanFrame.KeepFileNames.ToArray());
        Assert.IsNotNull(session.LatestScan);
        Assert.AreEqual(advancedCursor, session.LatestScan.AdvancedCursors["C"]);

        // The subsequent watch resumes from the rescan's advanced cursor, not the
        // original warm-start cursor.
        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;
        StringAssert.Contains(watchFrame.DrivesSpec, "C:9:400");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartFromCursors_BrokerDiesWhileParked_LatchesFaultAndBlocksWatch()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 200L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        RaiseBrokerDied(client, "broker crashed while parked");

        Assert.IsTrue(session.IsFaulted);
        Assert.AreEqual(JournalBrokerSessionState.Faulted, session.State);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.StartWatchAsync());
        Assert.AreEqual("broker crashed while parked", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartFromCursors_DisposeWhileParked_SendsSingleShutdownFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 200L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        var readTask = ReadOneFrameAsync(serverSide);
        await session.DisposeAsync();
        var shutdownFrame = await readTask;

        Assert.AreEqual(BrokerFrameKind.Shutdown, shutdownFrame.Kind);
    }

    [TestMethod]
    public async Task StartFromCursors_StartWatchMidTransmissionCancellation_MakesSessionTerminal()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 200L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var startTask = session.StartWatchAsync(cts.Token);
        await gate.Entered;
        await cts.CancelAsync();
        gate.Release();

        try
        {
            await startTask;
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // The frame write started, so cancellation leaves its transmission ambiguous.
        }

        try
        {
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
                session.StartWatchAsync(CancellationToken.None));
            Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task PublicStartFromCursors_InProcessBroker_EndToEnd()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var liveBatch = (
            new[] { JournalEntryFactory.Create(1, 210, "warm.txt") },
            new UsnJournalCursor(7UL, 260L));

        Task? brokerTask = null;
        var launchBroker = new Func<string, bool>(args =>
        {
            var parts = args.Split(' ');
            var pipeName = parts[Array.IndexOf(parts, "--pipe") + 1];
            brokerTask = Task.Run(async () =>
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);

                var fakeHost = CreateHost(
                    _ => new UsnJournalCursor(7UL, 200L),
                    (_, _, _) => [],
                    (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
                    (_, _, _) => OneBatchAsync(liveBatch));

                await fakeHost.ServeAsync(pipe, new RealBlockSectionWriter(), false, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 200L) };
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(launchBroker, cursors, cts.Token);

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.IsNull(session.LatestScan);

        await session.StartWatchAsync(cts.Token);
        await using var batches = session.WatchDriveAsync("C", cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.IsTrue(await batches.MoveNextAsync());
        Assert.AreEqual("warm.txt", batches.Current.Entries.Single().FileName);

        await session.DisposeAsync();
        await brokerTask!.WaitAsync(cts.Token);
    }

    // ── Test-extensions harness (ScanSessionTestHarness) ─────────────────────
    // These go through the public MFTLibTestExtensions surface a consumer would use,
    // proving the thin wrappers forward to the same internal seams the tests above hit.

    [TestMethod]
    public async Task Harness_StartScannedAsync_ForwardsArgumentsAndParksOnScan()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var keepFileNames = new[] { "note.txt" };

        BrokerFrame armAndScanFrame = default;
        var brokerTask = Task.Run(async () =>
        {
            armAndScanFrame = await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await ScanSessionTestHarness.StartScannedAsync(_ => Task.FromResult(client), DriveC, CreateOptions(BrokerScanProfile.DirectoryIndex, keepFileNames), CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.IsNotNull(session.LatestScan);
        CollectionAssert.AreEqual(keepFileNames, armAndScanFrame.KeepFileNames.ToArray());
        CollectionAssert.AreEqual(DriveC, session.Drives.ToArray());
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);

        await session.DisposeAsync();
    }
}
