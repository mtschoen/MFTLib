using System.Buffers;
using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task WatchDrive_StaleCursorError_FaultsThatDriveWithTheRescanMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", WatchSpecArmEpochs.ForDrive(watchFrame, "C"), "Drive C cannot resume its live watch from journal cursor 7:100: journal wrapped. " +
            "The records are gone, so this drive needs a rescan before it can be watched again.");
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        var deliveredBatchCount = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.WatchDriveAsync("C", timeout.Token))
            {
                deliveredBatchCount++;
            }
        });

        StringAssert.Contains(exception.Message, "7:100");
        StringAssert.Contains(exception.Message, "rescan");
        Assert.AreEqual(0, deliveredBatchCount);
        Assert.IsFalse(session.IsFaulted, "One drive's failure must not fault the session");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchDrive_UnarmedDrive_ThrowsArgumentException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        Assert.ThrowsException<ArgumentException>(() => session.WatchDriveAsync("D"));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchDrive_JournalInvalidatedMidWatch_ThrowsInvalidOperation()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", WatchSpecArmEpochs.ForDrive(watchFrame, "C"), "journal wrapped");
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.WatchDriveAsync("C", timeout.Token))
            {
            }
        });
        Assert.AreEqual("journal wrapped", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_PreSendCancellation_CanBeRetried()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Assert.ThrowsExceptionAsync<OperationCanceledException> requires an exact type
        // match, but the concrete exception the BCL throws for an already-cancelled
        // token (SemaphoreSlim.WaitAsync) is the subtype TaskCanceledException.
        try
        {
            await session.StartWatchAsync(cts.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync(CancellationToken.None);
        var watchFrame = await watchFrameTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        Assert.AreEqual(JournalBrokerSessionState.Watching, session.State);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_MidTransmissionCancellation_MakesSessionTerminal()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

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
    public async Task WatchDrive_Cancelled_StopsCleanly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        // Not a `using var`: the token is captured by CancelAfterReadsStream's callback
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after the demux has finished (awaited via DisposeAsync).
        var cts = new CancellationTokenSource();
        // The scan consumes 4 frames (Cursor, ScanReady, JournalBatch plus VolumeInfo = 8 reads); cancel
        // right after the demux reads the header+body of the one live JournalBatch frame
        // written below (the 10th ReadAsync call), landing the cancellation between
        // while-loop iterations instead of racing an already-blocked read.
        Action cancel = cts.Cancel;
        using var wrapped = new CancelAfterReadsStream(clientSide, 10, cancel);
        var client = MakeMinimalFakeClient(wrapped);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync(cts.Token);
        var watchFrame = await watchFrameTask;

        var entry = JournalEntryFactory.Create(1, 10, "a");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", WatchSpecArmEpochs.ForDrive(watchFrame, "C"),
            new UsnJournalCursor(7UL, 110L), [entry]);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in session.WatchDriveAsync("C", timeout.Token))
        {
            received.Add(batch);
        }

        // The demux delivered the one buffered frame, then the loop observed the
        // cancellation and ended cleanly (channel completed, no exception).
        Assert.AreEqual(1, received.Count);

        await session.DisposeAsync();
        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
        cts.Dispose();
    }

    [TestMethod]
    public async Task WatchDrive_WhileParked_ThrowsInvalidOperation()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        Assert.ThrowsException<InvalidOperationException>(() => session.WatchDriveAsync("C"));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StopWatch_WhileParked_IsNoOp()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        await session.StopWatchAsync();

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StopWatch_WhileWatching_ReturnsToParked_AndCanRestart()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        // The broker side must read the EndWatch the stop sends and reply with an
        // EndWatchAck so the handshake completes fast rather than via the ack timeout.
        var receivedKinds = new List<BrokerFrameKind>();
        var watchTask = Task.Run(async () =>
        {
            receivedKinds.Add((await ReadOneFrameAsync(serverSide)).Kind); // StartWatch
            receivedKinds.Add((await ReadOneFrameAsync(serverSide)).Kind); // EndWatch
            var ack = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteEndWatchAck(ack);
            await serverSide.WriteAsync(ack.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await session.StartWatchAsync();
        await session.StopWatchAsync();
        await watchTask;

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.StartWatch, BrokerFrameKind.EndWatch }, receivedKinds);
        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);

        // Restarting the watch on the same client (no second arm/spawn) must not throw.
        var restartFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var restartFrame = await restartFrameTask;
        Assert.AreEqual(BrokerFrameKind.StartWatch, restartFrame.Kind);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StopWatch_WhenClientStopCompletesSynchronously_AwaitsCapturedTask()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var startWatchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await startWatchFrameTask).Kind);

        // Complete the client demux before stopping so StopLiveWatchAsync takes only
        // synchronous completion paths. StopWatchAsync must retain the task even when
        // StopWatchCoreAsync clears the shared field before returning to its caller.
        var ack = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(ack);
        await serverSide.WriteAsync(ack.WrittenMemory);
        await serverSide.FlushAsync();
        var demuxTaskField = typeof(JournalBrokerClient).GetField(
            "_demuxTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)demuxTaskField.GetValue(client)!;

        await session.StopWatchAsync();

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide)).Kind);

        await session.DisposeAsync();
    }
}
