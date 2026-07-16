using System.Buffers;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerScanSessionTests
{
    static readonly string[] DriveC = { "C:\\" };
    static readonly string[] DrivesCAndD = { "C:\\", "D:\\" };

    [TestMethod]
    public async Task StartAsync_Scans_ParksWithLatestScanResult()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var scanRecord = new ScanRecord(RecordNumber: 5, ParentRecordNumber: 5, Size: 0,
            LastWriteTicks: 0, Attributes: 0x10, IsDirectory: true, Name: "C:", Path: "C:\\");
        var armedCursor = new UsnJournalCursor(7UL, 100L);
        var advancedCursor = new UsnJournalCursor(7UL, 200L);
        var catchUpEntry = UsnJournalEntry.Create(
            100, 5, 150, DateTime.UnixEpoch, UsnReason.FileCreate | UsnReason.Close,
            FileAttributes.Normal, "note.txt");

        var client = new JournalBrokerClient(
            pipe: clientSide,
            mmfReader: new FakeMmfReader(new[] { scanRecord }),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan request

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", armedCursor);
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 1, 1);
            BrokerProtocol.WriteJournalBatch(response, "C", advancedCursor, new[] { catchUpEntry });
            BrokerProtocol.WriteError(response, "D", "journal wrapped");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DrivesCAndD, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(1, session.LatestScan.Records.Count);
        Assert.AreEqual(armedCursor, session.LatestScan.ArmedCursors["C"]);
        Assert.AreEqual(advancedCursor, session.LatestScan.AdvancedCursors["C"]);
        Assert.AreEqual(1, session.LatestScan.CatchUpEntries["C"].Length);
        Assert.AreEqual("journal wrapped", session.LatestScan.Errors["D"]);
        Assert.IsFalse(session.IsFaulted);
        Assert.IsNull(session.FaultReason);
        CollectionAssert.AreEqual(DrivesCAndD, session.Drives.ToArray());
        Assert.AreEqual(BrokerScanProfile.Full, session.Profile);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_WithKeepFileNames_ForwardsNamesToArmAndScanFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var keepFileNames = new[] { "note.txt", "README.md" };

        BrokerFrame armAndScanFrame = default;
        var brokerTask = Task.Run(async () =>
        {
            armAndScanFrame = await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.DirectoryIndex, keepFileNames,
            CancellationToken.None);
        await brokerTask;

        CollectionAssert.AreEqual(keepFileNames, armAndScanFrame.KeepFileNames.ToArray());

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_WithoutKeepFileNames_SendsEmptyNameList()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        BrokerFrame armAndScanFrame = default;
        var brokerTask = Task.Run(async () =>
        {
            armAndScanFrame = await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(0, armAndScanFrame.KeepFileNames.Count);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_ConnectsExactlyOnce()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var connectCount = 0;

        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ =>
            {
                connectCount++;
                return Task.FromResult(client);
            },
            DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(1, connectCount);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_BrokerDiesDuringInitialScan_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var tracker = new DisposeTrackingStream(clientSide);
        var client = MakeMinimalFakeClient(tracker);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan request
            serverSide.Dispose(); // EOF before any drive responds
        });

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => JournalBrokerScanSession.StartAsync(
                _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None));
        await brokerTask;

        StringAssert.Contains(exception.Message, "Pipe EOF");
        Assert.IsTrue(tracker.Disposed, "the session must dispose the client instead of leaking it");
    }

    [TestMethod]
    public async Task StartAsync_Cancelled_Throws()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Assert.ThrowsExceptionAsync<OperationCanceledException> requires an exact type
        // match, but the concrete exception the BCL throws for an already-cancelled token
        // (e.g. SemaphoreSlim.WaitAsync) is the subtype TaskCanceledException - any
        // OperationCanceledException satisfies the documented contract.
        try
        {
            await JournalBrokerScanSession.StartAsync(
                _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: cts.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    [TestMethod]
    public async Task Dispose_WhileParked_SendsSingleShutdownFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        var readTask = ReadOneFrameAsync(serverSide);
        await session.DisposeAsync();
        var shutdownFrame = await readTask;

        Assert.AreEqual(BrokerFrameKind.Shutdown, shutdownFrame.Kind);
    }

    [TestMethod]
    public async Task Dispose_CalledTwice_DisposesClientOnce()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        var shutdownFrames = new List<BrokerFrameKind>();
        var readAllTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                    shutdownFrames.Add((await ReadOneFrameAsync(serverSide)).Kind);
            }
            catch (EndOfStreamException)
            {
                // Expected: the session disposed the client's pipe, ending the stream.
            }
        });

        await session.DisposeAsync();
        await session.DisposeAsync();
        await readAllTask;

        Assert.AreEqual(1, shutdownFrames.Count(kind => kind == BrokerFrameKind.Shutdown));
    }

    [TestMethod]
    public async Task Operation_AfterDispose_ThrowsObjectDisposed()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        await session.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(() => session.EnsureOperable());
    }

    [TestMethod]
    public async Task BrokerDeath_LatchesIsFaultedAndFaultReason()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        RaiseBrokerDied(client, "broker crashed");

        Assert.IsTrue(session.IsFaulted);
        Assert.AreEqual("broker crashed", session.FaultReason);
        Assert.AreEqual(JournalBrokerSessionState.Faulted, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Faulted_LateSubscriber_FiresImmediately()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        RaiseBrokerDied(client, "broker crashed");

        string? observedReason = null;
        session.Faulted += reason => observedReason = reason;

        Assert.AreEqual("broker crashed", observedReason);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Faulted_SubscribeBeforeDeath_InvokedOnDeath()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        string? observedReason = null;
        session.Faulted += reason => observedReason = reason;
        Assert.IsNull(observedReason); // stored, not invoked immediately: no death yet

        RaiseBrokerDied(client, "broker crashed");

        Assert.AreEqual("broker crashed", observedReason);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task EnsureOperable_WhileParked_DoesNotThrow()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        session.EnsureOperable(); // must not throw while Parked

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Faulted_Unsubscribe_StopsReceivingNotifications()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        var invocationCount = 0;
        Action<string> handler = _ => invocationCount++;
        session.Faulted += handler;
        session.Faulted -= handler;

        RaiseBrokerDied(client, "broker crashed");

        Assert.AreEqual(0, invocationCount);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task EnsureOperable_WhileFaulted_ThrowsInvalidOperationException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        RaiseBrokerDied(client, "broker crashed");

        var exception = Assert.ThrowsException<InvalidOperationException>(() => session.EnsureOperable());
        Assert.AreEqual("broker crashed", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task BrokerDeath_SecondDeathSignal_ReasonUnchanged()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        RaiseBrokerDied(client, "first reason");
        RaiseBrokerDied(client, "second reason");

        Assert.AreEqual("first reason", session.FaultReason);

        await session.DisposeAsync();
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task PublicStartAsync_InProcessBroker_EndToEnd()
    {
        Task? brokerTask = null;
        var launchBroker = new Func<string, bool>(args =>
        {
            var parts = args.Split(' ');
            var pipeName = parts[Array.IndexOf(parts, "--pipe") + 1];
            brokerTask = Task.Run(async () =>
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);

                var fakeHost = new JournalBrokerHost(
                    queryCursor: _ => new UsnJournalCursor(7UL, 0L),
                    scanDrive: _ => Array.Empty<ScanRecord>(),
                    readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

                await fakeHost.ServeAsync(pipe, new RealMmfWriter(), oneShot: true, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = await JournalBrokerScanSession.StartAsync(launchBroker, DriveC, cts.Token);

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(0, session.LatestScan.Records.Count);
        Assert.IsTrue(session.LatestScan.ArmedCursors.ContainsKey("C"));

        await brokerTask!.WaitAsync(cts.Token);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_UsesSameClientAsScan_NoSecondArmOrSpawn()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var connectCount = 0;
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ =>
            {
                connectCount++;
                return Task.FromResult(client);
            },
            DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        Assert.AreEqual(1, connectCount);
        Assert.AreEqual(JournalBrokerSessionState.Watching, session.State);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_WhenAlreadyWatching_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.StartWatchAsync());

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_NoDriveArmed_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan request
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "C", "access denied");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await brokerTask;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.StartWatchAsync());

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchDrive_HappyPath_YieldsBatchesFromAdvancedCursor()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        var cursor = new UsnJournalCursor(7UL, 210L);
        var entry = UsnJournalEntry.Create(
            1, 5, 110, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "f.txt");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", cursor, new[] { entry });
        BrokerProtocol.WriteEndWatchAck(response);
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in session.WatchDriveAsync("C", timeout.Token))
            received.Add(batch);

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(cursor, received[0].Cursor);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchDrive_UnarmedDrive_ThrowsArgumentException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", "journal wrapped");
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.WatchDriveAsync("C", timeout.Token)) { }
        });
        Assert.AreEqual("journal wrapped", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartWatch_Cancelled_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

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
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchDrive_Cancelled_StopsCleanly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        // Not a `using var`: the token is captured by CancelAfterReadsStream's callback
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after the demux has finished (awaited via DisposeAsync).
        var cts = new CancellationTokenSource();
        // The scan consumes 3 frames (Cursor, ScanReady, JournalBatch = 6 reads); cancel
        // right after the demux reads the header+body of the one live JournalBatch frame
        // written below (the 8th ReadAsync call), landing the cancellation between
        // while-loop iterations instead of racing an already-blocked read.
        // aislop-ignore-next-line AccessToDisposedClosure
        var wrapped = new CancelAfterReadsStream(clientSide, threshold: 8, () => cts.Cancel());
        var client = MakeMinimalFakeClient(wrapped);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync(cts.Token);
        await watchFrameTask;

        var entry = UsnJournalEntry.Create(1, 5, 10, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "a");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), new[] { entry });
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in session.WatchDriveAsync("C", timeout.Token))
            received.Add(batch);

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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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

    [TestCleanup]
    public void Cleanup() => JournalBrokerClient.EndWatchAckTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task StartWatch_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotResurrectWatching()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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
        var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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
        JournalBrokerClient.EndWatchAckTimeout = TimeSpan.FromMilliseconds(50);

        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.EndWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        var field = typeof(JournalBrokerScanSession).GetField("_batchSource", BindingFlags.NonPublic | BindingFlags.Instance)!;
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

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        await session.DisposeAsync();

        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static JournalBrokerClient MakeMinimalFakeClient(Stream pipe) =>
        new(pipe,
            mmfReader: new FakeMmfReader(Array.Empty<ScanRecord>()),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

    // Runs a broker task that reads one ArmAndScan request and replies with a
    // minimal happy-path Cursor + ScanReady + JournalBatch sequence for one drive.
    static Task RespondToArmAndScanAsync(Stream serverSide, string drive)
    {
        return Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, drive, new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, $"mftlib-null-{drive}", 0, 0);
            BrokerProtocol.WriteJournalBatch(response, drive, new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    // Invokes the private BrokerDied backing delegate directly, simulating broker
    // death without a live watch (Task 2 has no background reader to detect a real
    // EOF once parked; Task 3's live watch exercises the real detection path).
    static void RaiseBrokerDied(JournalBrokerClient client, string reason)
    {
        var field = typeof(JournalBrokerClient).GetField(
            nameof(JournalBrokerClient.BrokerDied), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = (Action<string>?)field.GetValue(client);
        handler?.Invoke(reason);
    }

    sealed class FakeMmfReader : IMmfReader
    {
        readonly ScanRecord[] _records;
        public FakeMmfReader(ScanRecord[] records) => _records = records;
        public ScanRecord[] Read(string mmfName, long byteLength) => _records;
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }

    // Wraps a stream and records whether it was disposed, so a test can assert the
    // session disposed its owned client without depending on the underlying stream's
    // own post-dispose exception semantics.
    sealed class DisposeTrackingStream : Stream
    {
        readonly Stream _inner;
        public bool Disposed { get; private set; }

        public DisposeTrackingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
