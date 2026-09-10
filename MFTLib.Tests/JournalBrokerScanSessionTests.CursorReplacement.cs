using System.Buffers;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task Harness_StartFromCursorsAsync_ParksWithoutScanning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 200L) };
        var session = await ScanSessionTestHarness.StartFromCursorsAsync(
            _ => Task.FromResult(client), cursors, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);

        Assert.AreEqual(JournalBrokerSessionState.Parked, session.State);
        Assert.IsNull(session.LatestScan);
        CollectionAssert.AreEqual(BareDriveC, session.Drives.ToArray());

        // No ArmAndScan was sent: the first frame the broker sees is the StartWatch.
        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;
        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);

        await session.DisposeAsync();
    }

    // ── Watch cursor replacement (ReplaceWatchCursors / WatchCursors) ────────

    [TestMethod]
    public async Task ReplaceWatchCursors_NullArgument_ThrowsArgumentNullException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        Assert.ThrowsException<ArgumentNullException>(() => session.ReplaceWatchCursors(null!));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_WhileParked_UpdatesWatchCursors()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var newCursor = new UsnJournalCursor(7UL, 500L);
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = newCursor });

        Assert.AreEqual(1, session.WatchCursors.Count);
        Assert.AreEqual(newCursor, session.WatchCursors["C"]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_WhileWatching_ThrowsInvalidOperation()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 500L) }));
        Assert.AreEqual("Live watch has already been started for this session", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_WhileOperationInFlight_ThrowsInvalidOperation()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gate = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        var client = MakeMinimalFakeClient(gate);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var startWatchTask = session.StartWatchAsync();
        await gate.Entered; // StartWatch write is blocked mid-flight; State is still Parked, _operationInFlight is true

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 500L) }));
        Assert.AreEqual("Another session operation is in progress", exception.Message);

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        gate.Release();
        await watchFrameTask;
        await startWatchTask;

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_WhileFaulted_ThrowsInvalidOperationWithFaultReason()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        RaiseBrokerDied(client, "broker crashed");

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 500L) }));
        Assert.AreEqual("broker crashed", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_WhileDisposed_ThrowsObjectDisposed()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        await session.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(() =>
            session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 500L) }));
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_NormalizesKeys_LastWriterWinsOnDuplicates()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var cursor1 = new UsnJournalCursor(7UL, 100L);
        var cursor2 = new UsnJournalCursor(7UL, 200L);
        var cursor3 = new UsnJournalCursor(7UL, 300L);

        // Supply C, C:, and C:\ in sequence so last writer wins
        var dictionary = new Dictionary<string, UsnJournalCursor>
        {
            ["C"] = cursor1,
            ["C:"] = cursor2,
            ["C:\\"] = cursor3,
        };

        session.ReplaceWatchCursors(dictionary);

        Assert.AreEqual(1, session.WatchCursors.Count);
        Assert.IsTrue(session.WatchCursors.ContainsKey("C"));
        Assert.IsTrue(session.WatchCursors.ContainsKey("c"));
        Assert.AreEqual(cursor3, session.WatchCursors["C"]);
        Assert.AreEqual(cursor3, session.WatchCursors["c"]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_MalformedDriveKeys_ThrowsArgumentException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var validCursor = new UsnJournalCursor(7UL, 100L);
        var malformedKeys = new[]
        {
            "C:garbage",
            "C,D",
            "",
            "   ",
            "1",
            "1:",
            "CD",
            "$$",
            @"\\.\Volume{12345678-1234-1234-1234-123456789012}",
            "C:\\foo\\bar"
        };

        foreach (var badKey in malformedKeys)
        {
            Assert.ThrowsException<ArgumentException>(() =>
                session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { [badKey] = validCursor }),
                $"Expected ArgumentException for malformed drive key '{badKey}'");
        }

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StartFromCursorsAsync_MalformedDriveKeys_ThrowsArgumentException()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var validCursor = new UsnJournalCursor(7UL, 100L);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            JournalBrokerScanSession.StartFromCursorsAsync(
                _ => Task.FromResult(client),
                new Dictionary<string, UsnJournalCursor> { ["C:garbage"] = validCursor },
                BrokerScanProfile.Full,
                cancellationToken: CancellationToken.None));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            JournalBrokerScanSession.StartFromCursorsAsync(
                _ => Task.FromResult(client),
                new Dictionary<string, UsnJournalCursor> { [""] = validCursor },
                BrokerScanProfile.Full,
                cancellationToken: CancellationToken.None));
    }

    [TestMethod]
    public async Task WatchDriveAsync_NormalizesDrivePath_FindsWatchedDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        await watchFrameTask;

        // Calling WatchDriveAsync with "C:\\" or "c:" successfully normalizes to "C"
        var stream1 = session.WatchDriveAsync(@"C:\");
        Assert.IsNotNull(stream1);

        var stream2 = session.WatchDriveAsync("c:");
        Assert.IsNotNull(stream2);

        // Malformed drive letter throws ArgumentException
        Assert.ThrowsException<ArgumentException>(() => session.WatchDriveAsync("C:garbage"));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_ReplacesRatherThanMerges_DropsOmittedDrives()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", BrokerFrame.NoArmEpoch, new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(9UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-D", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "D", BrokerFrame.NoArmEpoch, new UsnJournalCursor(9UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DrivesCAndD, CreateOptions(), cancellationToken: CancellationToken.None);
        await brokerTask;

        Assert.AreEqual(2, session.WatchCursors.Count);
        Assert.IsTrue(session.WatchCursors.ContainsKey("C"));
        Assert.IsTrue(session.WatchCursors.ContainsKey("D"));

        // Replace with only drive C
        var cursorC = new UsnJournalCursor(7UL, 400L);
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = cursorC });

        Assert.AreEqual(1, session.WatchCursors.Count);
        Assert.IsTrue(session.WatchCursors.ContainsKey("C"));
        Assert.IsFalse(session.WatchCursors.ContainsKey("D"));
        Assert.AreEqual(cursorC, session.WatchCursors["C"]);

        // Replace with empty dictionary
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor>());
        Assert.AreEqual(0, session.WatchCursors.Count);

        // StartWatchAsync on empty watch cursors throws "No drives to watch"
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => session.StartWatchAsync());
        Assert.AreEqual("No drives to watch", exception.Message);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_DefensiveCopy_CallerMutationDoesNotAffectSession()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var mutableDict = new Dictionary<string, UsnJournalCursor>
        {
            ["C"] = new(7UL, 100L)
        };

        session.ReplaceWatchCursors(mutableDict);

        // Mutate caller's dictionary after replacement
        mutableDict["C"] = new UsnJournalCursor(7UL, 999L);
        mutableDict["D"] = new UsnJournalCursor(9UL, 999L);

        Assert.AreEqual(1, session.WatchCursors.Count);
        Assert.AreEqual(new UsnJournalCursor(7UL, 100L), session.WatchCursors["C"]);
        Assert.IsFalse(session.WatchCursors.ContainsKey("D"));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_StartWatchAsync_SendsReplacedSetOverWire()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var replacedCursor = new UsnJournalCursor(7UL, 777L);
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C:\\"] = replacedCursor });

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        StringAssert.Contains(watchFrame.DrivesSpec, "C:7:777");

        await session.DisposeAsync();
    }
}
