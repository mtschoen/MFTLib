using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_LaunchDeclined_ThrowsAndDisposesServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var launchBroker = new Func<string, bool>(_ => false); // simulates a declined UAC prompt

        var exception =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

        StringAssert.Contains(exception.Message, "declined");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_NullLaunchBroker_ThrowsArgumentNullException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            JournalBrokerClient.SpawnAndConnectAsync(null!));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_NegativeTimeout_ThrowsArgumentOutOfRangeException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var launchBroker = new Func<string, bool>(_ => true);

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            JournalBrokerClient.SpawnAndConnectAsync(launchBroker, TimeSpan.FromSeconds(-5)));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_BrokerNeverConnects_TimesOutAndDisposesServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var launchBroker = new Func<string, bool>(_ => true); // launch started, but broker never connects

        var exception =
            await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker, TimeSpan.FromMilliseconds(50)));

        StringAssert.Contains(exception.Message, "Timed out waiting 50ms");
        StringAssert.Contains(exception.Message, "mftlib-broker-");
        StringAssert.Contains(exception.Message, "launched, but never connected");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_DefaultTimeoutOverridden_TimesOutAndDisposesServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        try
        {
            JournalBrokerClient._connectTimeout = TimeSpan.FromMilliseconds(50);
            var launchBroker = new Func<string, bool>(_ => true);

            var exception =
                await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                    JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

            StringAssert.Contains(exception.Message, "Timed out waiting 50ms");
            StringAssert.Contains(exception.Message, "mftlib-broker-");
            StringAssert.Contains(exception.Message, "launched, but never connected");
        }
        finally
        {
            JournalBrokerClient.ResetToDefaults();
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_CallerCancellationRequested_ThrowsOperationCanceledException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled
        var launchBroker = new Func<string, bool>(_ => true);

        try
        {
            await JournalBrokerClient.SpawnAndConnectAsync(launchBroker, TimeSpan.FromSeconds(30), cts.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    [TestMethod]
    public async Task DisposeAsync_PipeAlreadyClosed_SwallowsShutdownWriteFailure()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await clientSide.DisposeAsync(); // pipe already gone before DisposeAsync tries to write Shutdown

        await client.DisposeAsync(); // must not throw
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_DemuxCtsInconsistentWithDemuxTask_ThrowsInvalidOperationException()
    {
        // _demuxCts and _demuxTask are always set together in SendStartWatchAsync and
        // cleared together at the end of a stop, so there is no public-API path to a
        // state where one is set without the other. Reflection simulates that violated
        // invariant (e.g. a future bug) to exercise StopLiveWatchAsync's own guard.
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        var demuxCtsField =
            typeof(JournalBrokerClient).GetField("_demuxCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousCts = (CancellationTokenSource)demuxCtsField.GetValue(client)!;
        demuxCtsField.SetValue(client, null);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(client.StopLiveWatchAsync);

        await previousCts.CancelAsync();
        previousCts.Dispose();
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_NotWatching_IsNoOp()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        await client.StopLiveWatchAsync(); // no SendStartWatchAsync was ever called

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_PipeAlreadyClosed_SwallowsEndWatchWriteFailure()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        await clientSide.DisposeAsync(); // pipe already gone; WriteEndWatch will fail

        await client.StopLiveWatchAsync(); // must not throw or hang
        _ = serverSide;
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_NoAckWithinTimeout_ForcesDemuxDown()
    {
        JournalBrokerClient._endWatchAckTimeout = TimeSpan.FromMilliseconds(50);

        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        // The broker side never sends EndWatchAck (a wedged broker); StopLiveWatchAsync
        // must not hang - it forces the demux down once the (shrunk) timeout elapses.
        await client.StopLiveWatchAsync();
        Assert.IsTrue(client.LastStopTimedOut, "StopLiveWatchAsync must force demux down via timeout when broker sends no ack.");

        await client.DisposeAsync();
        _ = serverSide;
    }

    [TestMethod]
    public async Task SendStartWatchAsync_CalledTwiceWithoutStop_ThrowsInvalidOperationException()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) };

        await client.SendStartWatchAsync(cursors);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.SendStartWatchAsync(cursors));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateBatchSource_ChannelCompletesCleanly_EnumerationEndsWithoutError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        var enumerateTask = Task.Run(async () =>
        {
            await foreach (var batch in batchSource("C:\\", default, CancellationToken.None))
            {
                received.Add(batch);
            }
        });

        await ReadOneFrameAsync(serverSide); // consume the StartWatch request

        var ack = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(ack);
        await serverSide.WriteAsync(ack.WrittenMemory);
        await serverSide.FlushAsync();

        await enumerateTask; // must complete normally - no exception
        Assert.AreEqual(0, received.Count);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateBatchSource_DemuxReadThrows_SignalsBrokerDeathWithExceptionMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        string? deathMessage = null;
        client.BrokerDied += message => deathMessage = message;

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        // A truncated frame (claims 10 bytes, delivers 3, then EOF) makes ReadFrameAsync
        // throw instead of returning null, exercising the demux's catch(Exception) path
        // rather than the clean-EOF path other death tests already cover.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await serverSide.WriteAsync(header);
        await serverSide.WriteAsync(new byte[] { 1, 2, 3 });
        await serverSide.FlushAsync();
        await serverSide.DisposeAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, CancellationToken.None))
            {
            }
        });

        Assert.IsNotNull(deathMessage);
        StringAssert.Contains(deathMessage, "Truncated broker frame");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateBatchSource_CancelledBetweenFrames_ChannelCompletesCleanly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        // Not a `using var`: the token is captured by CancelAfterReadsStream's callback
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after the demux has finished (awaited below).
        var cts = new CancellationTokenSource();
        // Cancel right after the 2nd ReadAsync on the client's pipe completes (the
        // header, then body, of the one JournalBatch frame written below). This lands
        // the cancellation exactly between while-loop iterations - a plain boolean
        // check - instead of racing an already-blocked read (which would throw
        // instead of falling out of the loop normally).
        Action cancel = cts.Cancel;
        using var wrapped = new CancelAfterReadsStream(clientSide, 2, cancel);
        var client = new JournalBrokerClient(wrapped, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));

        var entry = JournalEntryFactory.Create(1, 10, "a");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), [entry]);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, cts.Token);

        var batchSource = client.CreateBatchSource();
        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in batchSource("C:\\", default, timeout.Token))
        {
            received.Add(batch);
        }

        // The demux delivered the one buffered frame, then the loop observed the
        // cancellation and ended cleanly (channel completed, no exception).
        Assert.AreEqual(1, received.Count);

        await client.DisposeAsync();
        cts.Dispose();
    }

    [TestMethod]
    public async Task CreateBatchSource_CancelledBetweenFrames_CompletesAllLiveChannels()
    {
        // Two-drive variant of the test above: exercises CompleteAllLiveChannels'
        // loop over multiple channels (not just a single one) when the demux's
        // while-loop exits via observed cancellation rather than an exception.
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        // Not a `using var`: the token is captured by CancelAfterReadsStream's callback
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after the demux has finished (awaited below).
        var cts = new CancellationTokenSource();
        // Cancel right after the 4th ReadAsync on the client's pipe completes (the
        // header+body of each of the two JournalBatch frames written below), landing
        // the cancellation between while-loop iterations once both frames are in.
        Action cancel = cts.Cancel;
        using var wrapped = new CancelAfterReadsStream(clientSide, 4, cancel);
        var client = new JournalBrokerClient(wrapped, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));

        var entryC = JournalEntryFactory.Create(1, 10, "a");
        var entryD = JournalEntryFactory.Create(2, 20, "b");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), [entryC]);
        BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(7UL, 210L), [entryD]);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L), ["D"] = new(7UL, 200L) }, cts.Token);

        var batchSource = client.CreateBatchSource();
        var receivedC = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        var receivedD = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var batch in batchSource("C:\\", default, timeout.Token))
        {
            receivedC.Add(batch);
        }

        await foreach (var batch in batchSource("D:\\", default, timeout.Token))
        {
            receivedD.Add(batch);
        }

        // Both drives' channels were completed by the same cancellation-observed
        // loop exit, not just the one the earlier single-drive test covers.
        Assert.AreEqual(1, receivedC.Count);
        Assert.AreEqual(1, receivedD.Count);

        await client.DisposeAsync();
        cts.Dispose();
    }
}
