using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerClientTests
{
    static readonly string[] DriveC = ["C:\\"];
    static readonly string[] DriveD = ["D:\\"];
    static readonly string[] KeepFileNamesGit = [".git"];

    // ---------------------------------------------------------------------------
    // Happy-path: full ArmScanAndCatchUp round-trip
    // ---------------------------------------------------------------------------

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ArmScanAndCatchUpAsync_ReturnsRecords_ArmedCursor_AndCatchUpEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped files require Windows.");
        }

        // Arrange: pre-create a real MMF and write known records into it.
        var records = new[]
        {
            new ScanRecord(5, 5, 0,
                0, 0x10, true,
                "C:", "C:\\"),
            // Non-ASCII name proves UTF-16 encoding is preserved end-to-end.
            new ScanRecord(100, 5, 2048,
                638_000_000_000_000_000L, 0x20, false,
                "nöte.txt", "C:\\nöte.txt")
        };
        var mapName = "mftlib-client-test-" + Guid.NewGuid().ToString("N");
        var byteLength = ScanPayload.ComputeSize(records);
        using var map = MemoryMappedFile.CreateNew(mapName, byteLength);
        await using (var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Write))
        {
            var buffer = new byte[byteLength];
            ScanPayload.Write(buffer, records);
            view.Write(buffer, 0, buffer.Length);
        }

        var armedCursor = new UsnJournalCursor(7UL, 100L);
        var advancedCursor = new UsnJournalCursor(7UL, 200L);
        var catchUpEntry = JournalEntryFactory.Create(
            100, 150, "nöte.txt", UsnReason.FileCreate | UsnReason.Close);

        var (clientSide, serverSide) = DuplexStream.CreatePair();

        // The "broker" task: read the ArmAndScan frame from the server side, then
        // write back Heartbeat -> Cursor -> ScanReady -> JournalBatch in response.
        var brokerTask = Task.Run(async () =>
        {
            // Read and discard the ArmAndScan request frame.
            await ReadOneFrameAsync(serverSide);

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteHeartbeat(response);
            BrokerProtocol.WriteCursor(response, "C", armedCursor);
            BrokerProtocol.WriteScanReady(response, mapName, records.Length, byteLength);
            BrokerProtocol.WriteJournalBatch(response, "C", advancedCursor,
                [catchUpEntry]);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeFakeClient(
            clientSide,
            new RealMmfReader(),
            mapName);

        // Act
        var consumed = new List<ScanRecord>();
        var result = await client.ArmScanAndCatchUpAsync(DriveC, new BrokerScanOptions
        {
            ConsumeRecords = (batch, _) =>
            {
                consumed.AddRange(batch);
                return ValueTask.CompletedTask;
            }
        });
        await brokerTask;

        // Assert
        Assert.AreEqual(2, consumed.Count);
        Assert.AreEqual("C:\\nöte.txt", consumed[1].Path);
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

        Assert.IsTrue(result.Errors.ContainsKey("D"));
        Assert.AreEqual("journal wrapped", result.Errors["D"]);
        Assert.IsFalse(result.ArmedCursors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // ArmScanAndCatchUpAsync(profile, keepFileNames): the wire frame carries both
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ProfileOverload_WithoutKeepFileNames_SendsEmptyList()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var brokerTask = Task.Run(async () =>
        {
            var request = await ReadOneFrameAsync(serverSide);
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, request.Kind);
            Assert.AreEqual(0, request.KeepFileNames.Count);

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "D", "journal wrapped");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeMinimalFakeClient(clientSide);
        var result = await client.ArmScanAndCatchUpAsync(DriveD,
            new BrokerScanOptions { Profile = BrokerScanProfile.Full }, CancellationToken.None);
        await brokerTask;

        Assert.IsTrue(result.Errors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_WithKeepFileNames_SendsThemOnTheWire()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var brokerTask = Task.Run(async () =>
        {
            var request = await ReadOneFrameAsync(serverSide);
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, request.Kind);
            CollectionAssert.Contains((ICollection)request.KeepFileNames, ".git");

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "D", "journal wrapped");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeMinimalFakeClient(clientSide);
        var result = await client.ArmScanAndCatchUpAsync(
            DriveD,
            new BrokerScanOptions { Profile = BrokerScanProfile.DirectoryIndex, KeepFileNames = KeepFileNamesGit });
        await brokerTask;

        Assert.IsTrue(result.Errors.ContainsKey("D"));

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
        var entry = JournalEntryFactory.Create(1, 110, "f.txt");

        // Write two JournalBatch frames from the "broker" side and then close.
        var brokerTask = Task.Run(async () =>
        {
            var response = new ArrayBufferWriter<byte>();
            // First batch for "E" - should be skipped by the C-drive source.
            BrokerProtocol.WriteJournalBatch(response, "E", cursor1, [entry]);
            // Second batch for "C" - should be yielded.
            BrokerProtocol.WriteJournalBatch(response, "C", cursor2, [entry]);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            await serverSide.DisposeAsync(); // EOF -> broker death -> throws InvalidOperationException
        });

        var client = MakeMinimalFakeClient(clientSide);
        // Start the live-watch demux (single pipe reader) before subscribing per drive.
        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        // Broker death (pipe EOF) now throws InvalidOperationException instead of completing.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var batch in batchSource("C:\\", default, CancellationToken.None))
            {
                received.Add(batch);
            }
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

        var stopwatch = Stopwatch.StartNew();
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
        var strayEntry = JournalEntryFactory.Create(1, 110, "stray.txt");

        // Broker side: after reading EndWatch, write a stray JournalBatch (a live
        // frame the host emitted before it noticed the stop) BEFORE the ack. The
        // demux must drain past it to the ack and still complete the stop cleanly.
        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // StartWatch
            await ReadOneFrameAsync(serverSide); // EndWatch

            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L), [strayEntry]);
            BrokerProtocol.WriteEndWatchAck(response);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.SendStartWatchAsync(cursors);

        var stopwatch = Stopwatch.StartNew();
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
    public void Cleanup()
    {
        JournalBrokerClient._endWatchAckTimeout = TimeSpan.FromSeconds(5);
    }

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
        await serverSide.DisposeAsync();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => client.ArmScanAndCatchUpAsync(DriveC));

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
        await serverSide.DisposeAsync();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => client.ArmScanAndCatchUpAsync(DriveC));

        await client.DisposeAsync();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_LaunchDeclined_ThrowsAndDisposesServer()
    {
        var launchBroker = new Func<string, bool>(_ => false); // simulates a declined UAC prompt

        var exception =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

        StringAssert.Contains(exception.Message, "declined");
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
        var stopwatch = Stopwatch.StartNew();
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
        var wrapped = new CancelAfterReadsStream(clientSide, 2, cancel);
        var client = new JournalBrokerClient(
            wrapped,
            new NullMmfReader(),
            (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

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
        var wrapped = new CancelAfterReadsStream(clientSide, 4, cancel);
        var client = new JournalBrokerClient(
            wrapped,
            new NullMmfReader(),
            (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

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

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

            Assert.IsNotNull(capturedArgs);
            StringAssert.EndsWith(capturedArgs, "--diag");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_EndToEnd_UsesRealPipeAndRealMmfSeams()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped files and named pipes require Windows.");
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

                // A minimal fake host: one drive, zero records, zero catch-up entries.
                // ServeAsync uses the REAL RealMmfWriter to write into the client's
                // real, named, page-file-backed MMF - exercising CreateRealDriveMmf.
                var fakeHost = new JournalBrokerHost(
                    _ => new UsnJournalCursor(7UL, 0L),
                    _ => Array.Empty<ScanRecord>(),
                    (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

                await fakeHost.ServeAsync(pipe, new RealMmfWriter(), true, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = await JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cts.Token);

        var result = await client.ArmScanAndCatchUpAsync(DriveC, cancellationToken: cts.Token);

        Assert.IsTrue(result.ArmedCursors.ContainsKey("C"));
        Assert.AreEqual(0, result.Errors.Count);

        await brokerTask!.WaitAsync(cts.Token);
        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Disposal & Streaming tests (Task 6)
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_DisposesMmf_AfterScanReadyPayloadConsumption_WhileUnreadDriveStaysLive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var trackerC = new TrackingDisposable();
        var trackerD = new TrackingDisposable();
        var reader = new FakeBatchMmfReader();
        reader.SetData("mmf-C", [new ScanRecord(1, 0, 100, 1000, 0x20, false, "c.txt", "C:\\c.txt")]);
        reader.SetData("mmf-D", [new ScanRecord(2, 0, 200, 2000, 0x20, false, "d.txt", "D:\\d.txt")]);

        var client = new JournalBrokerClient(
            clientSide,
            reader,
            (letter, _) => (letter == "C" ? "mmf-C" : "mmf-D", letter == "C" ? trackerC : trackerD));

        var collectedC = new List<ScanRecord>();
        var collectedD = new List<ScanRecord>();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // Read ArmAndScan

            // Drive C
            var responseC = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(responseC, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanReady(responseC, "mmf-C", 1, 100);
            await serverSide.WriteAsync(responseC.WrittenMemory);
            await serverSide.FlushAsync();

            await trackerC.DisposedTask; // wait until client consumed ScanReady for C and disposed the map

            // At this point, C should be disposed, D should still be live
            Assert.IsTrue(trackerC.IsDisposed, "Drive C's map must be disposed after consumption");
            Assert.IsFalse(trackerD.IsDisposed, "Drive D's map must stay live while unread");

            // Complete C's catchup
            var catchupC = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteJournalBatch(catchupC, "C", new UsnJournalCursor(7UL, 110L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(catchupC.WrittenMemory);
            await serverSide.FlushAsync();

            // Drive D
            var responseD = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(responseD, "D", new UsnJournalCursor(8UL, 200L));
            BrokerProtocol.WriteScanReady(responseD, "mmf-D", 1, 100);
            BrokerProtocol.WriteJournalBatch(responseD, "D", new UsnJournalCursor(8UL, 210L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(responseD.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.ArmScanAndCatchUpAsync(
            ["C:\\", "D:\\"],
            new BrokerScanOptions
            {
                ConsumeRecords = (records, _) =>
                {
                    if (records.Any(r => r.Name == "c.txt"))
                    {
                        collectedC.AddRange(records);
                    }
                    else
                    {
                        collectedD.AddRange(records);
                    }

                    return ValueTask.CompletedTask;
                }
            });

        await brokerTask;

        Assert.IsTrue(trackerC.IsDisposed);
        Assert.IsTrue(trackerD.IsDisposed);
        Assert.AreEqual(1, collectedC.Count);
        Assert.AreEqual(1, collectedD.Count);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ErrorFrame_DisposesMmfLifetimeImmediately()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var trackerD = new TrackingDisposable();

        var client = new JournalBrokerClient(
            clientSide,
            new FakeBatchMmfReader(),
            (_, _) => ("mmf-D", trackerD));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // Read ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "D", "drive failed");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var result = await client.ArmScanAndCatchUpAsync(DriveD,
            new BrokerScanOptions { ConsumeRecords = (_, _) => ValueTask.CompletedTask });
        await brokerTask;

        Assert.IsTrue(trackerD.IsDisposed, "Error frame must immediately dispose the failed drive's map");
        Assert.IsTrue(result.Errors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ReaderThrows_StillDisposesMmfLifetime()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var trackerC = new TrackingDisposable();
        var throwingReader = new ThrowingMmfReader();

        var client = new JournalBrokerClient(
            clientSide,
            throwingReader,
            (_, _) => ("mmf-C", trackerC));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // Read ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanReady(response, "mmf-C", 1, 100);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            client.ArmScanAndCatchUpAsync(DriveC,
                new BrokerScanOptions { ConsumeRecords = (_, _) => ValueTask.CompletedTask }));

        await brokerTask;

        Assert.IsTrue(trackerC.IsDisposed, "Throwing reader must still trigger disposal in finally");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_StreamsBatches_ConcatenatedEqualOriginal_BatchSizeNotExceeding4096()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var reader = new FakeBatchMmfReader();

        var totalRecords = new List<ScanRecord>();
        for (ulong i = 0; i < 10000; i++)
        {
            totalRecords.Add(new ScanRecord(i, 0, i * 10, 1000, 0x20, false, $"file{i}.txt", $"C:\\file{i}.txt"));
        }

        reader.SetData("mmf-C", totalRecords);

        var client = new JournalBrokerClient(
            clientSide,
            reader,
            (_, _) => ("mmf-C", new TrackingDisposable()));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanReady(response, "mmf-C", 10000, 500000);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var receivedBatches = new List<IReadOnlyList<ScanRecord>>();
        var collectedRecords = new List<ScanRecord>();

        await client.ArmScanAndCatchUpAsync(DriveC, new BrokerScanOptions
        {
            ConsumeRecords = (batch, _) =>
            {
                Assert.IsTrue(batch.Count <= 4096, $"Batch size {batch.Count} must not exceed 4096");
                receivedBatches.Add(batch);
                collectedRecords.AddRange(batch);
                return ValueTask.CompletedTask;
            }
        });

        await brokerTask;

        Assert.IsTrue(receivedBatches.Count >= 3);
        CollectionAssert.AreEqual(totalRecords, collectedRecords);

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
            pipe,
            fakeMmfReader,
            (_, _) => (fakeMmfName, NoOpDisposable.Instance));
    }

    // Minimal fake client for tests that do not exercise MMF reads.
    static JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(
            pipe,
            new NullMmfReader(),
            (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));
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

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_Options_DispatchesProgressCallback()
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
            BrokerProtocol.WriteScanReady(response, "mftlib-progress-C", 100, 2000);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 200L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var reader = new FakeBatchMmfReader();
        reader.SetData("mftlib-progress-C", Array.Empty<ScanRecord>());
        var client = MakeFakeClient(clientSide, reader, "mftlib-progress-C");

        var options = new BrokerScanOptions
        {
            Progress = progress
        };

        var result = await client.ArmScanAndCatchUpAsync(DriveC, options);
        await brokerTask;

        Assert.AreEqual(2, progress.Reports.Count);
        Assert.AreEqual(50, progress.Reports[0].RecordsProcessed);
        Assert.AreEqual(100, progress.Reports[1].RecordsProcessed);
        Assert.AreEqual("C", progress.Reports[0].DriveLetter);
        Assert.AreEqual(0, result.Errors.Count);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ScanProgressFrame_DoesNotCompleteDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var progress = new SyncProgress<BrokerScanProgress>();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 10, 200, 100, 2000, TimeSpan.FromMilliseconds(10)));
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();

            // Delay before completing the scan
            await Task.Delay(50);
            var completeResponse = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteScanReady(completeResponse, "mftlib-scan-C", 100, 2000);
            BrokerProtocol.WriteJournalBatch(completeResponse, "C", new UsnJournalCursor(7UL, 200L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(completeResponse.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var reader = new FakeBatchMmfReader();
        reader.SetData("mftlib-scan-C", Array.Empty<ScanRecord>());
        var client = MakeFakeClient(clientSide, reader, "mftlib-scan-C");

        var result = await client.ArmScanAndCatchUpAsync(DriveC, new BrokerScanOptions { Progress = progress });
        await brokerTask;

        Assert.AreEqual(1, progress.Reports.Count);
        Assert.IsTrue(result.ArmedCursors.ContainsKey("C"));
        Assert.IsTrue(result.AdvancedCursors.ContainsKey("C"));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task DemuxLoopAsync_ScanProgressFrame_IgnoredDuringLiveWatch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // StartWatch
            var response = new ArrayBufferWriter<byte>();
            // Late progress frame arriving during live watch
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 100, 2000, 100, 2000, TimeSpan.FromSeconds(1)));
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L),
                [JournalEntryFactory.Create(1, 110, "live.txt")]);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in batchSource("C:\\", default, cts.Token))
        {
            received.Add(batch);
            break;
        }

        await brokerTask;
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual("live.txt", received[0].Item1[0].FileName);

        await client.DisposeAsync();
    }

    // Test double: IMmfReader that returns an empty array (for tests that do not
    // need real MMF data and inject an Error path instead).
    sealed class NullMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength)
        {
            return Array.Empty<ScanRecord>();
        }
    }

    sealed class FakeBatchMmfReader : IStreamingMmfReader
    {
        readonly Dictionary<string, List<ScanRecord>> _data = [];

        public ScanRecord[] Read(string mmfName, long byteLength)
        {
            return _data.TryGetValue(mmfName, out var list) ? list.ToArray() : Array.Empty<ScanRecord>();
        }

        public IEnumerable<ScanRecord[]> ReadBatches(
            string mmfName, long byteLength, int batchSize, CancellationToken cancellationToken)
        {
            if (!_data.TryGetValue(mmfName, out var list))
            {
                yield break;
            }

            for (var i = 0; i < list.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, list.Count - i);
                var batch = new ScanRecord[count];
                list.CopyTo(i, batch, 0, count);
                yield return batch;
            }
        }

        public void SetData(string mmfName, IEnumerable<ScanRecord> records)
        {
            _data[mmfName] = records.ToList();
        }
    }

    sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    sealed class ThrowingMmfReader : IStreamingMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength)
        {
            throw new InvalidOperationException("Simulated reader failure");
        }

        public IEnumerable<ScanRecord[]> ReadBatches(
            string mmfName, long byteLength, int batchSize, CancellationToken cancellationToken)
        {
            return Enumerable.Repeat(0, 1).Select<int, ScanRecord[]>(_ =>
                throw new InvalidOperationException("Simulated reader failure"));
        }
    }

    sealed class TrackingDisposable : IDisposable
    {
        readonly TaskCompletionSource _disposedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }
        public Task DisposedTask => _disposedTcs.Task;

        public void Dispose()
        {
            IsDisposed = true;
            _disposedTcs.TrySetResult();
        }
    }

    // Disposable no-op lifetime handle for tests.
    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
