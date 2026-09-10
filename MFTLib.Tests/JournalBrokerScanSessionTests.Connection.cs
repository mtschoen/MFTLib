using System.Buffers;
using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task EnsureOperable_WhileParked_DoesNotThrow()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        RaiseBrokerDied(client, "broker crashed");

        var exception = Assert.ThrowsException<InvalidOperationException>(session.EnsureOperable);
        Assert.AreEqual("broker crashed", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task BrokerDeath_SecondDeathSignal_ReasonUnchanged()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var brokerTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        RaiseBrokerDied(client, "first reason");
        RaiseBrokerDied(client, "second reason");

        Assert.AreEqual("first reason", session.FaultReason);

        await session.DisposeAsync();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task PublicStartAsync_InProcessBroker_EndToEnd()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

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
                    _ => new UsnJournalCursor(7UL, 0L),
                    (_, _, _) => [],
                    (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

                await fakeHost.ServeAsync(pipe, new RealBlockSectionWriter(), true, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = await JournalBrokerScanSession.StartAsync(launchBroker, DriveC, CreateOptions(), cts.Token);

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.IsTrue(session.LatestScan!.ArmedCursors.ContainsKey("C"));

        // These blocks come from the real section factory, so neither the base class
        // cleanup nor client disposal owns them. The session does, and this is where it
        // proves it: the DeleteOnClose files are gone once the session is disposed.
        var blockPaths = session.LatestScan.BlockOutcomes.Values.Select(outcome => outcome.Block.Path).ToArray();
        Assert.AreNotEqual(0, blockPaths.Length);

        await brokerTask!.WaitAsync(cts.Token);
        await session.DisposeAsync();

        foreach (var blockPath in blockPaths)
        {
            Assert.IsFalse(File.Exists(blockPath), $"session disposal must release {blockPath}");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task PublicStartAsync_WithOptions_InProcessBroker_ParksWithRequestedProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

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
                    _ => new UsnJournalCursor(7UL, 0L),
                    (_, _, _) => [],
                    (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

                await fakeHost.ServeAsync(pipe, new RealBlockSectionWriter(), true, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var options = new BrokerScanOptions
        {
            BlockTargets = CreateTargets(),
            Profile = BrokerScanProfile.DirectoryIndex,
            KeepFileNames = new[] { "note.txt" }
        };

        // The launchBroker + BrokerScanOptions public overload is never exercised
        // through the internal connectAsync seam used elsewhere in this file, so it
        // needs its own real in-process broker round trip.
        var session = await JournalBrokerScanSession.StartAsync(launchBroker, DriveC, options, cts.Token);

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, session.Profile);
        Assert.IsTrue(session.LatestScan!.ArmedCursors.ContainsKey("C"));

        // Same ownership boundary as the end-to-end test above.
        var blockPaths = session.LatestScan.BlockOutcomes.Values.Select(outcome => outcome.Block.Path).ToArray();
        Assert.AreNotEqual(0, blockPaths.Length);

        await brokerTask!.WaitAsync(cts.Token);
        await session.DisposeAsync();

        foreach (var blockPath in blockPaths)
        {
            Assert.IsFalse(File.Exists(blockPath), $"session disposal must release {blockPath}");
        }
    }

    [TestMethod]
    public async Task StartWatch_UsesSameClientAsScan_NoSecondArmOrSpawn()
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;
        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);

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
            BrokerProtocol.WriteError(response, "C", BrokerFrame.NoArmEpoch, "access denied");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
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

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        var cursor = new UsnJournalCursor(7UL, 210L);
        var entry = JournalEntryFactory.Create(1, 110, "f.txt");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", WatchSpecArmEpochs.ForDrive(watchFrame, "C"), cursor, [entry]);
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
        Assert.AreEqual(cursor, received[0].Cursor);

        await session.DisposeAsync();
    }

}
