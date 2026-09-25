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
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await client.SendStartWatchAsync(
                new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, cancellation.Token);

            // The double dispose below is the behavior under test, so it is deliberately not
            // expressed as an `await using`: disposing twice is the assertion, not incidental cleanup.
            await client.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
            await client.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_DemuxCtsInconsistentWithDemuxTask_ThrowsInvalidOperationException()
    {
        // _demuxCts and _demuxTask are always set together in SendStartWatchAsync and
        // cleared together at the end of a stop, so there is no public-API path to a
        // state where one is set without the other. Reflection simulates that violated
        // invariant (e.g. a future bug) to exercise StopLiveWatchAsync's own guard.
        var (clientSide, _) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        var demuxCtsField =
            typeof(JournalBrokerClient).GetField("_demuxCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousCts = (CancellationTokenSource)demuxCtsField.GetValue(client)!;
        demuxCtsField.SetValue(client, null);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.StopLiveWatchAsync());

        await previousCts.CancelAsync();
        previousCts.Dispose();
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
        await using var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        await clientSide.DisposeAsync(); // pipe already gone; WriteEndWatch will fail

        await client.StopLiveWatchAsync(); // must not throw or hang
        _ = serverSide;
    }

    [TestMethod]
    public void TryNormalizeDriveLetter_Null_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            JournalBrokerClient.TryNormalizeDriveLetter(null!, out _));
    }

    [TestMethod]
    public void TryNormalizeDriveLetter_InvalidDrive_ReturnsFalseAndAnEmptyNormalizedDrive()
    {
        var normalized = JournalBrokerClient.TryNormalizeDriveLetter("not a drive", out var normalizedDrive);

        Assert.IsFalse(normalized);
        Assert.AreEqual(string.Empty, normalizedDrive);
    }

    [DataTestMethod]
    [DataRow("C", "C")]
    [DataRow("c:\\", "C")]
    [DataRow("\\\\.\\d:", "D")]
    public void TryNormalizeDriveLetter_ValidDrive_ReturnsTrueAndUppercaseLetter(
        string drive, string expectedNormalizedDrive)
    {
        var normalized = JournalBrokerClient.TryNormalizeDriveLetter(drive, out var normalizedDrive);

        Assert.IsTrue(normalized);
        Assert.AreEqual(expectedNormalizedDrive, normalizedDrive);
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_Cancellation_LeavesClientReusableAndIgnoresStaleAck()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var client = MakeMinimalFakeClient(clientSide);

        // First watch session: generation 1
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var firstStart = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(1U, firstStart.WatchGeneration);

        // Cancel the stop wait
        using var stopCts = new CancellationTokenSource();
        var stopTask = client.StopLiveWatchAsync(stopCts.Token);
        var endWatch = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(BrokerFrameKind.EndWatch, endWatch.Kind);
        stopCts.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => stopTask);

        // A stale ack arrives from the broker for generation 1 after caller cancelled stop
        var staleAck = new System.Buffers.ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(staleAck, 1U);
        await serverSide.WriteAsync(staleAck.WrittenMemory);
        await serverSide.FlushAsync();

        // Start a second watch session: client must be reusable and allocate generation 2
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["D"] = new(8UL, 200L) });
        var secondStart = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(2U, secondStart.WatchGeneration);

        // Complete the second watch with a matching generation 2 ack
        var matchingAck = new System.Buffers.ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(matchingAck, 2U);
        await serverSide.WriteAsync(matchingAck.WrittenMemory);
        await serverSide.FlushAsync();

        // Stop live watch for generation 2 completes cleanly
        await client.StopLiveWatchAsync();
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_FutureGenerationAck_FaultsDemux()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var client = MakeMinimalFakeClient(clientSide);

        string? deathMessage = null;
        client.BrokerDied += message => deathMessage = message;

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var start = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(1U, start.WatchGeneration);

        // Server writes an ack with future generation (5 > 1)
        var futureAck = new System.Buffers.ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(futureAck, 5U);
        await serverSide.WriteAsync(futureAck.WrittenMemory);
        await serverSide.FlushAsync();

        var batchSource = client.CreateBatchSource();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C", default, CancellationToken.None))
            {
            }
        });

        Assert.IsNotNull(deathMessage);
        StringAssert.Contains(deathMessage, "future watch generation");
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_BrokerEOF_CompletesCleanlyWithoutTimeout()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, timeout.Token);
        var start = await ReadOneFrameAsync(serverSide).WaitAsync(timeout.Token);
        Assert.AreEqual(1U, start.WatchGeneration);

        // Server closes stream unexpectedly (EOF) while client stops
        var stopTask = client.StopLiveWatchAsync();
        await serverSide.DisposeAsync();

        // Must complete cleanly without hanging
        await stopTask.WaitAsync(timeout.Token);
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_NoToken_WaitsIndefinitelyUntilMatchingAck()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, timeout.Token);
        var start = await ReadOneFrameAsync(serverSide).WaitAsync(timeout.Token);
        Assert.AreEqual(1U, start.WatchGeneration);

        // Call StopLiveWatchAsync with default token (CancellationToken.None)
        var stopTask = client.StopLiveWatchAsync();

        // Yield execution to allow stopTask to begin awaiting demux
        await Task.Yield();
        Assert.IsFalse(stopTask.IsCompleted, "StopLiveWatchAsync without token must wait for EndWatchAck.");

        // Now send matching ack
        var ack = new System.Buffers.ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(ack, start.WatchGeneration);
        await serverSide.WriteAsync(ack.WrittenMemory, timeout.Token);
        await serverSide.FlushAsync(timeout.Token);

        // Now stopTask completes cleanly
        await stopTask.WaitAsync(timeout.Token);
        Assert.IsTrue(stopTask.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_AfterDispose_IsANoOp()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        var client = MakeMinimalFakeClient(clientSide);

        await client.DisposeAsync();

        // A stop after disposal has nothing left to stop: it must return quietly
        // instead of throwing ObjectDisposedException over an already-clean client.
        await client.StopLiveWatchAsync();
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_WithALatchedControlFailure_JoinsTheEndedDemuxAndReturns()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["D"] = new(9UL, 100L) }, token);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await ReadControlRequestAsync(serverSide, token)).Kind);

        // A caller-cancelled query with a request on the wire aborts the control
        // exchange, which latches the control failure and stops the demux.
        using var queryCancellation = new CancellationTokenSource();
        var query = client.QueryVolumesAsync(["C"], queryCancellation.Token);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes, (await ReadControlRequestAsync(serverSide, token)).Kind);
        await queryCancellation.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => query);

        // The stop must join the ended demux and reclaim the watch without
        // re-surfacing the latched control failure.
        await client.StopLiveWatchAsync().WaitAsync(token);
    }

    [TestMethod]
    public async Task SendDisarmDriveAsync_WhenDisposalCancelsItMidWrite_ThrowsObjectDisposed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        // The DisarmDrive frame write parks mid-frame inside the exchange, observing its
        // operation token, so disposal's cancellation of the control token unwinds the
        // write on its own: no release timing can race the cancellation callback.
        await using var gated = new CancellableGateFrameWriteStream(transport, BrokerFrameKind.DisarmDrive);
        // Deliberately not an `await using`: the explicit mid-test disposal is the
        // behavior under test, mirroring DisposeAsync_CalledTwice_DoesNotThrow.
        var client = MakeMinimalFakeClient(gated);
        try
        {
            await client.SendStartWatchAsync(
                new Dictionary<string, UsnJournalCursor> { ["D"] = new(9UL, 100L) }, token);

            var disarm = client.SendDisarmDriveAsync("D", token);
            await gated.Entered.WaitAsync(token);
            var dispose = client.DisposeAsync().AsTask();

            // The write's cancellation arrives because the client is being disposed, so the
            // caller faces ObjectDisposedException rather than a bare OperationCanceledException.
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => disarm.WaitAsync(token));

            gated.Release();
            await dispose.WaitAsync(token);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

}
