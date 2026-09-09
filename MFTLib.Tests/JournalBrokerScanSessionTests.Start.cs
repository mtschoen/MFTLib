using System.Buffers;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task StartAsync_Scans_ParksWithLatestScanResult()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var armedCursor = new UsnJournalCursor(7UL, 100L);
        var advancedCursor = new UsnJournalCursor(7UL, 200L);
        var catchUpEntry = JournalEntryFactory.Create(
            100, 150, "note.txt", UsnReason.FileCreate | UsnReason.Close);

        var client = new JournalBrokerClient(clientSide, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan request

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", armedCursor);
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 1, 1, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", advancedCursor, [catchUpEntry]);
            BrokerProtocol.WriteError(response, "D", "journal wrapped");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DrivesCAndD, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(armedCursor, session.LatestScan!.ArmedCursors["C"]);
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
    public async Task StartAsync_WithOptions_DispatchesProgressCallback()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var progress = new SyncProgress<BrokerScanProgress>();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 50, 1000, 100, 2000, TimeSpan.FromMilliseconds(50)));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 100, 2000, 100, 2000, TimeSpan.FromMilliseconds(100)));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 100, 2000, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 200L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeMinimalFakeClient(clientSide);
        var options = new BrokerScanOptions
        {
            BlockTargets = CreateTargets(),
            Progress = progress
        };

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, options, CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(2, progress.Reports.Count);
        Assert.AreEqual(50, progress.Reports[0].RecordsProcessed);
        Assert.AreEqual(100, progress.Reports[1].RecordsProcessed);
        Assert.AreEqual("C", progress.Reports[0].DriveLetter);
        Assert.AreEqual(0, session.LatestScan!.Errors.Count);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ScanSessionTestHarness_StartScannedAsync_WithOptions_DispatchesProgressCallback()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var progress = new SyncProgress<BrokerScanProgress>();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 100, 2000, 100, 2000, TimeSpan.FromMilliseconds(100)));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 100, 2000, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 200L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeMinimalFakeClient(clientSide);
        var options = new BrokerScanOptions
        {
            BlockTargets = CreateTargets(),
            Profile = BrokerScanProfile.DirectoryIndex,
            KeepFileNames = [".git"],
            Progress = progress
        };

        var session = await ScanSessionTestHarness.StartScannedAsync(
            _ => Task.FromResult(client), DriveC, options);
        await brokerTask;

        Assert.AreEqual(1, progress.Reports.Count);
        Assert.AreEqual(100, progress.Reports[0].RecordsProcessed);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);

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
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(BrokerScanProfile.DirectoryIndex, keepFileNames), CancellationToken.None);
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
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ =>
            {
                connectCount++;
                return Task.FromResult(client);
            }, DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(1, connectCount);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_BrokerDiesDuringInitialScan_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var tracker = new DisposeTrackingStream(clientSide);
        var client = MakeMinimalFakeClient(tracker);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan request
            await serverSide.DisposeAsync(); // EOF before any drive responds
        });

        var exception = await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None));
        await brokerTask;

        StringAssert.Contains(exception.Message, "disconnected");
        Assert.IsTrue(tracker.Disposed, "the session must dispose the client instead of leaking it");
    }

    [TestMethod]
    public async Task StartAsync_Cancelled_Throws()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Assert.ThrowsExceptionAsync<OperationCanceledException> requires an exact type
        // match, but the concrete exception the BCL throws for an already-cancelled token
        // (e.g. SemaphoreSlim.WaitAsync) is the subtype TaskCanceledException - any
        // OperationCanceledException satisfies the documented contract.
        try
        {
            await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: cts.Token);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        var shutdownFrames = new List<BrokerFrameKind>();
        var readAllTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    shutdownFrames.Add((await ReadOneFrameAsync(serverSide)).Kind);
                }
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        await session.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(session.EnsureOperable);
    }

    [TestMethod]
    public async Task BrokerDeath_LatchesIsFaultedAndFaultReason()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        string? observedReason = null;
        session.Faulted += reason => observedReason = reason;
        Assert.IsNull(observedReason); // stored, not invoked immediately: no death yet

        RaiseBrokerDied(client, "broker crashed");

        Assert.AreEqual("broker crashed", observedReason);

        await session.DisposeAsync();
    }
}
