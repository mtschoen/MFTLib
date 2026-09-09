using System.Buffers;
using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task RescanAsync_WithOptions_DispatchesProgressCallback()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var initialScanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await initialScanTask;

        var progress = new SyncProgress<BrokerScanProgress>();
        var rescanBrokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan for Rescan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 300L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 25, 500, 100, 2000, TimeSpan.FromMilliseconds(25)));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 75, 1500, 100, 2000, TimeSpan.FromMilliseconds(75)));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 100, 2000, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 400L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await session.RescanAsync(new BrokerScanOptions { BlockTargets = CreateTargets(), Progress = progress });
        await rescanBrokerTask;

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(2, progress.Reports.Count);
        Assert.AreEqual(25, progress.Reports[0].RecordsProcessed);
        Assert.AreEqual(75, progress.Reports[1].RecordsProcessed);
        Assert.AreEqual("C", progress.Reports[0].DriveLetter);
        Assert.AreEqual(new UsnJournalCursor(7UL, 400L), session.LatestScan!.AdvancedCursors["C"]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task RescanAsync_WithDrivesAndOptions_DispatchesProgressCallbackAndUpdatesDrives()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var initialScanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await initialScanTask;

        var progress = new SyncProgress<BrokerScanProgress>();
        var rescanBrokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan for Rescan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(7UL, 300L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("D", 50, 1000, 100, 2000, TimeSpan.FromMilliseconds(50)));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-D", 100, 2000, 0);
            BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(7UL, 400L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await session.RescanAsync(DriveD, new BrokerScanOptions { BlockTargets = CreateTargets(), Profile = BrokerScanProfile.DirectoryIndex, Progress = progress });
        await rescanBrokerTask;

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(1, progress.Reports.Count);
        Assert.AreEqual("D", progress.Reports[0].DriveLetter);
        CollectionAssert.AreEqual(DriveD, session.Drives.ToArray());
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_WithNewDrives_StoresDrivesAndProfileReusedByTheNextExplicitRescan()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var firstRescanTask = RespondToArmAndScanAsync(serverSide, "D");
        await session.RescanAsync(DriveD, CreateOptions(BrokerScanProfile.DirectoryIndex));
        await firstRescanTask;

        var secondRescanFrameTask = ReadOneFrameAsync(serverSide);
        var secondRescanTask = Task.Run(async () =>
        {
            var frame = await secondRescanFrameTask;
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(9UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-D", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(9UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            return frame;
        });

        await session.RescanAsync(CreateOptions(session.Profile));
        var secondRescanFrame = await secondRescanTask;

        StringAssert.Contains(secondRescanFrame.DrivesSpec,
            $"D:0:0:mftlib-null-D:{(int)BrokerScanProfile.DirectoryIndex}");
        CollectionAssert.AreEqual(DriveD, session.Drives.ToArray());
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_WithDestinationsAndDrives_ForwardsProfileAndKeepFileNames()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var keepFileNames = new[] { "note.txt" };

        var scanTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(BrokerScanProfile.DirectoryIndex, keepFileNames), CancellationToken.None);
        await scanTask;

        var rescanFrameTask = ReadOneFrameAsync(serverSide);
        var rescanTask = Task.Run(async () =>
        {
            var frame = await rescanFrameTask;
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(9UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-D", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(9UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            return frame;
        });

        await session.RescanAsync(DriveD, CreateOptions(session.Profile, keepFileNames), CancellationToken.None);
        var rescanFrame = await rescanTask;

        StringAssert.Contains(rescanFrame.DrivesSpec, $"D:0:0:mftlib-null-D:{(int)BrokerScanProfile.DirectoryIndex}");
        CollectionAssert.AreEqual(keepFileNames, rescanFrame.KeepFileNames.ToArray());
        CollectionAssert.AreEqual(DriveD, session.Drives.ToArray());
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_BrokerDiesDuringScan_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var rescanTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan request
            await serverSide.DisposeAsync(); // EOF before any drive responds
        });

        var exception = await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => session.RescanAsync(CreateOptions(session.Profile)));
        await rescanTask;

        StringAssert.Contains(exception.Message, "disconnected");
        Assert.IsTrue(session.IsFaulted);
        Assert.AreEqual(JournalBrokerSessionState.Faulted, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_PreSendCancellation_LeavesSessionParked()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Assert.ThrowsExceptionAsync<OperationCanceledException> requires an exact
        // type match, but the concrete exception the BCL throws for an already-
        // cancelled token (SemaphoreSlim.WaitAsync) is the subtype TaskCanceledException.
        try
        {
            await session.RescanAsync(CreateOptions(session.Profile), cts.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_MidResponseCancellation_MakesSessionTerminalBeforeNextOperation()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource();
        using var wrapped = new CancelAfterReadsStream(clientSide, 12, cts.Cancel);
        var client = MakeMinimalFakeClient(wrapped);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var brokerTask = Task.Run(async () =>
        {
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await ReadOneFrameAsync(serverSide)).Kind);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
            await serverSide.FlushAsync(CancellationToken.None);
        }, CancellationToken.None);

        try
        {
            await session.RescanAsync(CreateOptions(session.Profile), cts.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected after the response collector has consumed its first frame.
        }

        await brokerTask;

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
    public async Task Rescan_FaultedDuringHandshake_ThrowsEndOfStream_DoesNotOverwriteLatestScan()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.ArmAndScan, 2);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;
        var originalScan = session.LatestScan;

        await ReplyToVolumeQueryAsync(serverSide, BrokerFrame.QueryVolumes("C:0:0"));
        var rescanTask = session.RescanAsync(CreateOptions(session.Profile));
        await gate.Entered; // the rescan's ArmAndScan write is now blocked mid-flight

        RaiseBrokerDied(client, "broker crashed mid-rescan");
        Assert.AreEqual(JournalBrokerSessionState.Faulted, session.State);

        await serverSide.DisposeAsync(); // let the rescan's read loop observe EOF once unblocked
        gate.Release();

        var exception = await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => rescanTask);
        StringAssert.Contains(exception.Message, "disconnected");
        Assert.AreSame(originalScan, session.LatestScan);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotOverwriteLatestScan()
    {
        // Unlike StartWatch/StopWatch (a write, then a separately-owned background
        // reader), ArmScanAndCatchUpAsync reads synchronously after its own write, so
        // a real concurrent DisposeAsync here would race its own pipe teardown against
        // this call's foreground read non-deterministically. Force the Disposed state
        // directly (same reflection approach as WatchDrive_BatchSourceInvariantBroken)
        // to exercise the lock-recheck invariant without a flaky transport race.
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.ArmAndScan, 2);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;
        var originalScan = session.LatestScan;

        var rescanBrokerTask = RespondToArmAndScanAsync(serverSide, "C");
        var rescanOptions = CreateOptions(session.Profile);
        var rejectedBlockPath = rescanOptions.BlockTargets!["C"].Path;
        var rescanTask = session.RescanAsync(rescanOptions);
        await gate.Entered; // the rescan's ArmAndScan write is now blocked mid-flight

        var stateField =
            typeof(JournalBrokerScanSession).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
        stateField.SetValue(session, JournalBrokerSessionState.Disposed);

        gate.Release();
        await rescanBrokerTask;

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => rescanTask);
        Assert.AreSame(originalScan, session.LatestScan);
        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);

        // The rescan completed and handed back caller-owned blocks that publication then
        // rejected, so nothing else will ever own them. The DeleteOnClose block file is
        // gone only if the session disposed them.
        Assert.IsFalse(File.Exists(rejectedBlockPath),
            "a result rejected by the terminal-state recheck must have its blocks disposed");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_WhileRescanInFlight_ThrowsWithoutTouchingPipe()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.ArmAndScan, 2);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var rescanBrokerTask = RespondToArmAndScanAsync(serverSide, "C");
        var rescanTask = session.RescanAsync(CreateOptions(session.Profile));
        await gate.Entered; // rescan's ArmAndScan write is blocked mid-flight; State is still Parked

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.StartWatchAsync());
        StringAssert.Contains(exception.Message, "Another session operation is in progress");
        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);

        gate.Release();
        await rescanBrokerTask;
        await rescanTask;

        // Flag cleared once the rescan finished: StartWatchAsync now succeeds, and
        // the frame it sends is the first (and only) StartWatch frame on the wire -
        // proof the blocked attempt above never touched the pipe.
        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;
        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);

        await session.DisposeAsync();
    }
}
