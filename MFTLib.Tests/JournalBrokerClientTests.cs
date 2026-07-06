using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerClientTests
{
    static readonly string[] DriveC = { "C:\\" };
    static readonly string[] DriveD = { "D:\\" };

    // ---------------------------------------------------------------------------
    // Happy-path: full ArmScanAndCatchUp round-trip
    // ---------------------------------------------------------------------------

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ArmScanAndCatchUpAsync_ReturnsRecords_ArmedCursor_AndCatchUpEntries()
    {
        // Arrange: pre-create a real MMF and write known records into it.
        var records = new[]
        {
            new ScanRecord(RecordNumber: 5, ParentRecordNumber: 5, Size: 0,
                LastWriteTicks: 0, Attributes: 0x10, IsDirectory: true,
                Name: "C:", Path: "C:\\"),
            // Non-ASCII name proves UTF-16 encoding is preserved end-to-end.
            new ScanRecord(RecordNumber: 100, ParentRecordNumber: 5, Size: 2048,
                LastWriteTicks: 638_000_000_000_000_000L, Attributes: 0x20, IsDirectory: false,
                Name: "nöte.txt", Path: "C:\\nöte.txt"),
        };
        var mapName = "mftlib-client-test-" + Guid.NewGuid().ToString("N");
        var byteLength = ScanPayload.ComputeSize(records);
        using var map = MemoryMappedFile.CreateNew(mapName, byteLength);
        using (var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Write))
        {
            var buffer = new byte[byteLength];
            ScanPayload.Write(buffer, records);
            view.Write(buffer, 0, buffer.Length);
        }

        var armedCursor = new UsnJournalCursor(7UL, 100L);
        var advancedCursor = new UsnJournalCursor(7UL, 200L);
        var catchUpEntry = UsnJournalEntry.Create(
            100, 5, 150, DateTime.UnixEpoch, UsnReason.FileCreate | UsnReason.Close,
            FileAttributes.Normal, "nöte.txt");

        var (clientSide, serverSide) = DuplexStream.CreatePair();

        // The "broker" task: read the ArmAndScan frame from the server side, then
        // write back Cursor -> ScanReady -> JournalBatch in response.
        var brokerTask = Task.Run(async () =>
        {
            // Read and discard the ArmAndScan request frame.
            await ReadOneFrameAsync(serverSide);

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", armedCursor);
            BrokerProtocol.WriteScanReady(response, mapName, records.Length, byteLength);
            BrokerProtocol.WriteJournalBatch(response, "C", advancedCursor,
                new[] { catchUpEntry });
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeFakeClient(
            clientSide,
            fakeMmfReader: new RealMmfReader(),
            fakeMmfName: mapName);

        // Act
        var result = await client.ArmScanAndCatchUpAsync(DriveC);
        await brokerTask;

        // Assert
        Assert.AreEqual(2, result.Records.Count);
        Assert.AreEqual("C:\\nöte.txt", result.Records[1].Path);
        Assert.IsTrue(result.ArmedCursors.ContainsKey("C"));
        Assert.AreEqual(armedCursor, result.ArmedCursors["C"]);
        Assert.IsTrue(result.AdvancedCursors.ContainsKey("C"));
        Assert.AreEqual(advancedCursor, result.AdvancedCursors["C"]);
        Assert.IsTrue(result.CatchUpEntries.ContainsKey("C"));
        Assert.AreEqual(1, result.CatchUpEntries["C"].Length);
        Assert.AreEqual("nöte.txt", result.CatchUpEntries["C"][0].FileName);
        Assert.AreEqual(0, result.Errors.Count);

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Error-frame path: broker reports a per-drive error
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ErrorFrame_RecordsErrorAndCompletesForThatDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "D", "journal wrapped");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeMinimalFakeClient(clientSide);
        var result = await client.ArmScanAndCatchUpAsync(DriveD);
        await brokerTask;

        Assert.AreEqual(0, result.Records.Count);
        Assert.IsTrue(result.Errors.ContainsKey("D"));
        Assert.AreEqual("journal wrapped", result.Errors["D"]);
        Assert.IsFalse(result.ArmedCursors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // DisposeAsync sends a Shutdown frame
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task DisposeAsync_SendsShutdownFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        // Read the Shutdown frame concurrently: DisposeAsync flushes the frame then
        // disposes clientSide (which completes the serverSide reader pipe). Attempting
        // to read after the pipe reader is completed throws, so we start the read task
        // first and let DisposeAsync signal EOF to terminate the concurrent read.
        var readTask = ReadOneFrameAsync(serverSide);
        await client.DisposeAsync();

        var shutdownFrame = await readTask;
        Assert.AreEqual(BrokerFrameKind.Shutdown, shutdownFrame.Kind);
    }

    // ---------------------------------------------------------------------------
    // CreateBatchSource: yields JournalBatch frames for the requested drive
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task CreateBatchSource_YieldsBatchesForMatchingDrive_SkipsOtherDrives()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var cursor1 = new UsnJournalCursor(7UL, 110L);
        var cursor2 = new UsnJournalCursor(7UL, 120L);
        var entry = UsnJournalEntry.Create(
            1, 5, 110, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "f.txt");

        // Write two JournalBatch frames from the "broker" side and then close.
        var brokerTask = Task.Run(async () =>
        {
            var response = new ArrayBufferWriter<byte>();
            // First batch for "E" - should be skipped by the C-drive source.
            BrokerProtocol.WriteJournalBatch(response, "E", cursor1, new[] { entry });
            // Second batch for "C" - should be yielded.
            BrokerProtocol.WriteJournalBatch(response, "C", cursor2, new[] { entry });
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            serverSide.Dispose(); // EOF -> broker death -> throws InvalidOperationException
        });

        var client = MakeMinimalFakeClient(clientSide);
        // Start the live-watch demux (single pipe reader) before subscribing per drive.
        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new UsnJournalCursor(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        // Broker death (pipe EOF) now throws InvalidOperationException instead of completing.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var batch in batchSource("C:\\", default, CancellationToken.None))
                received.Add(batch);
        });

        await brokerTask;

        // Only the "C" batch should be yielded; the "E" batch is silently skipped.
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(cursor2, received[0].Item2);

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // StopLiveWatchAsync: reset live-watch state so the watch can restart
    // ---------------------------------------------------------------------------

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task StopLiveWatchAsync_ResetsState_SoWatchCanRestart()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) };

        // The broker side must read the EndWatch the stop sends and reply with an
        // EndWatchAck so the handshake completes; without it the stop would block on
        // its ack timeout. Read StartWatch, then EndWatch, then ack.
        var receivedKinds = new List<BrokerFrameKind>();
        var brokerTask = Task.Run(async () =>
        {
            var startFrame = await ReadOneFrameAsync(serverSide);
            receivedKinds.Add(startFrame.Kind);

            var endFrame = await ReadOneFrameAsync(serverSide);
            receivedKinds.Add(endFrame.Kind);

            var ack = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteEndWatchAck(ack);
            await serverSide.WriteAsync(ack.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.SendStartWatchAsync(cursors);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.StopLiveWatchAsync();
        stopwatch.Stop();

        // The handshake (EndWatch -> EndWatchAck) must complete fast, NOT via the 5s
        // ack-timeout fallback.
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"StopLiveWatchAsync took {stopwatch.Elapsed}, expected the fast ack handshake");

        await brokerTask;
        CollectionAssert.AreEqual(new[] { BrokerFrameKind.StartWatch, BrokerFrameKind.EndWatch }, receivedKinds);

        // Restart must NOT throw "Live watch has already been started".
        await client.SendStartWatchAsync(cursors);
        var restartFrame = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(BrokerFrameKind.StartWatch, restartFrame.Kind);

        await client.DisposeAsync();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task StopLiveWatchAsync_StrayBatchBeforeAck_StillStopsAndCanRestart()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) };
        var strayEntry = UsnJournalEntry.Create(
            1, 5, 110, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "stray.txt");

        // Broker side: after reading EndWatch, write a stray JournalBatch (a live
        // frame the host emitted before it noticed the stop) BEFORE the ack. The
        // demux must drain past it to the ack and still complete the stop cleanly.
        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // StartWatch
            await ReadOneFrameAsync(serverSide); // EndWatch

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), new[] { strayEntry });
            BrokerProtocol.WriteEndWatchAck(response);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.SendStartWatchAsync(cursors);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.StopLiveWatchAsync();
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"StopLiveWatchAsync took {stopwatch.Elapsed}, expected the fast ack handshake despite the stray batch");

        await brokerTask;

        // The client is healthy for restart after draining the stray batch.
        await client.SendStartWatchAsync(cursors);
        var restartFrame = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(BrokerFrameKind.StartWatch, restartFrame.Kind);

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Remaining edge cases: truncated frames, no-op stop, write failures, timeout
    // forcing, duplicate start guard, clean channel completion, broker-death via a
    // real protocol error, and the real SpawnAndConnectAsync/CreateRealDriveMmf path.
    // ---------------------------------------------------------------------------

    [TestCleanup]
    public void Cleanup() => JournalBrokerClient.EndWatchAckTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_TruncatedFrame_ThrowsEndOfStreamException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        // A valid 4-byte length prefix claiming a 10-byte frame, but only 3 bytes of
        // body before the pipe closes - simulates the broker dying mid-frame.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await serverSide.WriteAsync(header);
        await serverSide.WriteAsync(new byte[] { 1, 2, 3 });
        await serverSide.FlushAsync();
        serverSide.Dispose();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(
            () => client.ArmScanAndCatchUpAsync(DriveC));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_HeaderOnlyThenEof_ThrowsEndOfStreamException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        // A 4-byte length prefix claiming a 10-byte frame, but zero body bytes
        // before the pipe closes - the distinct "EOF exactly at the frame boundary"
        // case, as opposed to EOF partway through an already-started body read.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await serverSide.WriteAsync(header);
        await serverSide.FlushAsync();
        serverSide.Dispose();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(
            () => client.ArmScanAndCatchUpAsync(DriveC));

        await client.DisposeAsync();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_LaunchDeclined_ThrowsAndDisposesServer()
    {
        var launchBroker = new Func<string, bool>(_ => false); // simulates a declined UAC prompt

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

        StringAssert.Contains(exception.Message, "declined");
    }

    [TestMethod]
    public async Task DisposeAsync_PipeAlreadyClosed_SwallowsShutdownWriteFailure()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        clientSide.Dispose(); // pipe already gone before DisposeAsync tries to write Shutdown

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

        var demuxCtsField = typeof(JournalBrokerClient).GetField("_demuxCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousCts = (CancellationTokenSource)demuxCtsField.GetValue(client)!;
        demuxCtsField.SetValue(client, null);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.StopLiveWatchAsync());

        previousCts.Cancel();
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

        clientSide.Dispose(); // pipe already gone; WriteEndWatch will fail

        await client.StopLiveWatchAsync(); // must not throw or hang
        _ = serverSide;
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_NoAckWithinTimeout_ForcesDemuxDown()
    {
        JournalBrokerClient.EndWatchAckTimeout = TimeSpan.FromMilliseconds(50);

        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        // The broker side never sends EndWatchAck (a wedged broker); StopLiveWatchAsync
        // must not hang - it forces the demux down once the (shrunk) timeout elapses.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.StopLiveWatchAsync();
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"StopLiveWatchAsync took {stopwatch.Elapsed}, expected the shrunk timeout to force it down");

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

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => client.SendStartWatchAsync(cursors));

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
                received.Add(batch);
        });

        await ReadOneFrameAsync(serverSide); // consume the StartWatch request
        await Task.Delay(20); // give the subscriber a moment to register its channel

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
        serverSide.Dispose();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, CancellationToken.None)) { }
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
        // aislop-ignore-next-line AccessToDisposedClosure
        var wrapped = new CancelAfterReadsStream(clientSide, threshold: 2, () => cts.Cancel());
        var client = new JournalBrokerClient(
            pipe: wrapped,
            mmfReader: new NullMmfReader(),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

        var entry = UsnJournalEntry.Create(1, 5, 10, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "a");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), new[] { entry });
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, cts.Token);

        var batchSource = client.CreateBatchSource();
        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in batchSource("C:\\", default, timeout.Token))
            received.Add(batch);

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
        // aislop-ignore-next-line AccessToDisposedClosure
        var wrapped = new CancelAfterReadsStream(clientSide, threshold: 4, () => cts.Cancel());
        var client = new JournalBrokerClient(
            pipe: wrapped,
            mmfReader: new NullMmfReader(),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

        var entryC = UsnJournalEntry.Create(1, 5, 10, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "a");
        var entryD = UsnJournalEntry.Create(2, 5, 20, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "b");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), new[] { entryC });
        BrokerProtocol.WriteJournalBatch(response, "D", new UsnJournalCursor(7UL, 210L), new[] { entryD });
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L), ["D"] = new(7UL, 200L) }, cts.Token);

        var batchSource = client.CreateBatchSource();
        var receivedC = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        var receivedD = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var batch in batchSource("C:\\", default, timeout.Token))
            receivedC.Add(batch);
        await foreach (var batch in batchSource("D:\\", default, timeout.Token))
            receivedD.Add(batch);

        // Both drives' channels were completed by the same cancellation-observed
        // loop exit, not just the one the earlier single-drive test covers.
        Assert.AreEqual(1, receivedC.Count);
        Assert.AreEqual(1, receivedD.Count);

        await client.DisposeAsync();
        cts.Dispose();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_DiagEnvVarSet_AppendsDiagFlag()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        try
        {
            string? capturedArgs = null;
            var launchBroker = new Func<string, bool>(args =>
            {
                capturedArgs = args;
                return false; // decline immediately; this test only cares about the args string
            });

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

            Assert.IsNotNull(capturedArgs);
            StringAssert.EndsWith(capturedArgs, "--diag");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        }
    }

    // Wraps a stream and invokes a callback once its ReadAsync has been called
    // `threshold` times - used to inject cancellation deterministically between two
    // frame reads instead of racing an already-blocked read.
    sealed class CancelAfterReadsStream : Stream
    {
        readonly Stream _inner;
        readonly int _threshold;
        readonly Action _onThreshold;
        int _reads;

        public CancelAfterReadsStream(Stream inner, int threshold, Action onThreshold)
        {
            _inner = inner;
            _threshold = threshold;
            _onThreshold = onThreshold;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Increment(ref _reads) == _threshold)
                _onThreshold();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_EndToEnd_UsesRealPipeAndRealMmfSeams()
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

                // A minimal fake host: one drive, zero records, zero catch-up entries.
                // ServeAsync uses the REAL RealMmfWriter to write into the client's
                // real, named, page-file-backed MMF - exercising CreateRealDriveMmf.
                var fakeHost = new JournalBrokerHost(
                    queryCursor: _ => new UsnJournalCursor(7UL, 0L),
                    scanDrive: _ => Array.Empty<ScanRecord>(),
                    readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

                await fakeHost.ServeAsync(pipe, new RealMmfWriter(), oneShot: true, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = await JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cts.Token);

        var result = await client.ArmScanAndCatchUpAsync(DriveC, cts.Token);

        Assert.AreEqual(0, result.Records.Count);
        Assert.IsTrue(result.ArmedCursors.ContainsKey("C"));
        Assert.AreEqual(0, result.Errors.Count);

        await brokerTask!.WaitAsync(cts.Token);
        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    // Full-featured fake client with a real RealMmfReader and a known MMF name.
    static JournalBrokerClient MakeFakeClient(
        Stream pipe, IMmfReader fakeMmfReader, string fakeMmfName)
    {
        return new JournalBrokerClient(
            pipe: pipe,
            mmfReader: fakeMmfReader,
            createDriveMmf: (_, _) => (fakeMmfName, NoOpDisposable.Instance));
    }

    // Minimal fake client for tests that do not exercise MMF reads.
    static JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(
            pipe: pipe,
            mmfReader: new NullMmfReader(),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    // Test double: IMmfReader that returns an empty array (for tests that do not
    // need real MMF data and inject an Error path instead).
    sealed class NullMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength) => Array.Empty<ScanRecord>();
    }

    // Disposable no-op lifetime handle for tests.
    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
