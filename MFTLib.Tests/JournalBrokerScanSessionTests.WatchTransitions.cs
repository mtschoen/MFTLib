using System.Buffers;
using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task StopWatch_ConcurrentCalls_SendOneEndWatch_AndRestartReceivesBatches()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.EndWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var brokerTask = Task.Run(async () =>
        {
            Assert.AreEqual(BrokerFrameKind.StartWatch, (await ReadOneFrameAsync(serverSide)).Kind);

            var endWatchCount = 0;
            var frame = await ReadOneFrameAsync(serverSide);
            while (frame.Kind == BrokerFrameKind.EndWatch)
            {
                endWatchCount++;
                var ack = new ArrayBufferWriter<byte>();
                BrokerProtocol.WriteEndWatchAck(ack);
                await serverSide.WriteAsync(ack.WrittenMemory);
                await serverSide.FlushAsync();
                frame = await ReadOneFrameAsync(serverSide);
            }

            Assert.AreEqual(BrokerFrameKind.StartWatch, frame.Kind);

            var entry = JournalEntryFactory.Create(1, 110, "after-restart.txt");
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteJournalBatch(
                response, "C", new UsnJournalCursor(7UL, 110L), [entry]);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            return endWatchCount;
        });

        await session.StartWatchAsync();

        var firstStop = session.StopWatchAsync();
        await gate.Entered;
        var secondStop = session.StopWatchAsync();
        gate.Release();

        await Task.WhenAll(firstStop, secondStop);
        await session.StartWatchAsync();

        var endWatchCount = await brokerTask;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var batches = session.WatchDriveAsync("C", timeout.Token).GetAsyncEnumerator(timeout.Token);

        Assert.AreEqual(1, endWatchCount);
        Assert.IsTrue(await batches.MoveNextAsync());
        Assert.AreEqual("after-restart.txt", batches.Current.Entries.Single().FileName);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotResurrectWatching()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var startTask = session.StartWatchAsync();
        await gate.Entered; // the StartWatch write is now blocked mid-flight, holding the client's write lock

        var disposeTask = session.DisposeAsync();
        // DisposeAsync sets State = Disposed synchronously before its own await, so
        // this is already true even though disposeTask has not completed.
        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);

        gate.Release();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => startTask);
        await disposeTask;

        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
    }

    [TestMethod]
    public async Task StartWatch_FaultedDuringHandshake_ThrowsInvalidOperation_DoesNotResurrectWatching()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var startTask = session.StartWatchAsync();
        await gate.Entered;

        RaiseBrokerDied(client, "broker crashed mid-watch-start");
        Assert.AreEqual(JournalBrokerSessionState.Faulted, session.State);

        gate.Release();

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => startTask);
        Assert.AreEqual("broker crashed mid-watch-start", exception.Message);
        Assert.AreEqual(JournalBrokerSessionState.Faulted, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StopWatch_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotResurrectParked()
    {
        JournalBrokerClient._endWatchAckTimeout = TimeSpan.FromMilliseconds(50);

        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.EndWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        var stopTask = session.StopWatchAsync();
        await gate.Entered; // the EndWatch write is now blocked mid-flight, holding the client's write lock

        var disposeTask = session.DisposeAsync();
        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);

        gate.Release();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => stopTask);
        await disposeTask;

        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
    }

    [TestMethod]
    public async Task WatchDrive_AfterStopWatch_ThrowsInvalidOperation_NotNullReference()
    {
        // Deterministic stand-in for the WatchDriveAsync check/read race (Watching
        // check and _batchSource read now share one lock section with StopWatchAsync's
        // clear, so the race is closed by construction rather than by timing). This
        // proves the invariant the fix protects: once _batchSource is genuinely
        // cleared by a real stop, the state guard - not a null read - is what a
        // caller observes.
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

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

        var exception = Assert.ThrowsException<InvalidOperationException>(() => session.WatchDriveAsync("C"));
        Assert.AreEqual("Not currently watching; call StartWatchAsync first", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchDrive_BatchSourceInvariantBroken_ThrowsInvalidOperation()
    {
        // Defensive-invariant test: StartWatchAsync always sets _batchSource together
        // with State = Watching under the same lock, so this combination cannot arise
        // through the public API. Reflection forces it to exercise the "Watching state
        // has no cached batch source" diagnostic (mirrors BrokerFrame.RequireDrive's
        // default(BrokerFrame) tests) instead of leaving the throw branch permanently
        // dead for coverage purposes.
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        var field = typeof(JournalBrokerScanSession).GetField("_batchSource",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(session, null);

        var exception = Assert.ThrowsException<InvalidOperationException>(() => session.WatchDriveAsync("C"));
        Assert.AreEqual("Watching state has no cached batch source", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Dispose_WhileWatching_TearsDownDemux()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        await session.DisposeAsync();

        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
    }

    [TestMethod]
    public async Task StopThenRescanThenStartWatch_ReusesOneBroker()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var connectCount = 0;
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ =>
            {
                connectCount++;
                return Task.FromResult(client);
            }, DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var receivedKinds = new List<BrokerFrameKind>();
        var stopTask = Task.Run(async () =>
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
        await stopTask;

        var rescanTask = RespondToArmAndScanAsync(serverSide, "C");
        await session.RescanAsync(CreateOptions(session.Profile));
        await rescanTask;

        var restartFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var restartFrame = await restartFrameTask;

        CollectionAssert.AreEqual(
            new[] { BrokerFrameKind.StartWatch, BrokerFrameKind.EndWatch }, receivedKinds);
        Assert.AreEqual(BrokerFrameKind.StartWatch, restartFrame.Kind);
        Assert.AreEqual(1, connectCount);
        Assert.AreEqual(JournalBrokerSessionState.Watching, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_WhileWatching_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.RescanAsync(CreateOptions(session.Profile)));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Rescan_WithDestinations_ReusesInitialDrives()
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
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(9UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-D", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(9UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DrivesCAndD, CreateOptions(BrokerScanProfile.DirectoryIndex, keepFileNames), CancellationToken.None);
        await scanTask;

        var rescanFrameTask = ReadOneFrameAsync(serverSide);
        var rescanTask = Task.Run(async () =>
        {
            var frame = await rescanFrameTask;
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(9UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-D", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(9UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            return frame;
        });

        await session.RescanAsync(CreateOptions(session.Profile, keepFileNames));
        var rescanFrame = await rescanTask;

        StringAssert.Contains(rescanFrame.DrivesSpec, $"C:0:0:mftlib-null-C:{(int)BrokerScanProfile.DirectoryIndex}");
        StringAssert.Contains(rescanFrame.DrivesSpec, $"D:0:0:mftlib-null-D:{(int)BrokerScanProfile.DirectoryIndex}");
        CollectionAssert.AreEqual(keepFileNames, rescanFrame.KeepFileNames.ToArray());
        CollectionAssert.AreEqual(DrivesCAndD, session.Drives.ToArray());
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);

        await session.DisposeAsync();
    }
}
