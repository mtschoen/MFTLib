using MFTLib.Index;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    // ---------------------------------------------------------------------------
    // Happy-path: full ArmScanAndCatchUp round-trip
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ReturnsBlock_ArmedCursor_AndCatchUpEntries()
    {
        await using var harness = new InProcessBlockBrokerHarness(recordBatches: (_, _, _) =>
            [[new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                new MftRecord(100, 5, new MftRecordFields(1, FileAttributes.Normal, 2048), "nöte.txt", null)]],
            catchUp: (_, _) => ([JournalEntryFactory.Create(100, 12400, "nöte.txt")], new UsnJournalCursor(71, 12500)));
        var result = await harness.Client.ArmScanAndCatchUpAsync(DriveC, new BrokerScanOptions
        {
            BlockTargets = new Dictionary<string, BlockScanTarget>
            {
                ["C"] = new(harness.Request.BlockPath, 123, true)
            }
        }, harness.CancellationToken);
        using var block = result.BlockOutcomes["C"].Block;

        Assert.AreSame(harness.CreatedBlock, block);
        Assert.AreEqual("nöte.txt", NamePool.ReadRowName(block, 100).ToString());
        Assert.AreEqual(2048L, block.Rows[100].Size);
        Assert.AreEqual(InProcessBlockBrokerHarness.ArmedCursor, result.ArmedCursors["C"]);
        Assert.AreEqual(new UsnJournalCursor(71, 12500), result.AdvancedCursors["C"]);
        Assert.AreEqual("nöte.txt", result.CatchUpEntries["C"].Single().FileName);
        Assert.AreEqual(0, result.Errors.Count);
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
        var result = await client.ArmScanAndCatchUpAsync(DriveD, CreateOptions());
        await brokerTask;

        Assert.IsTrue(result.Errors.ContainsKey("D"));
        Assert.AreEqual("journal wrapped", result.Errors["D"]);
        Assert.IsFalse(result.ArmedCursors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Warning-frame path: broker degrades a drive (catch-up failed) instead of
    // failing it outright - unlike Error, the drive still completes normally.
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_WarningFrame_RecordsWarning_AndDriveStillCompletes()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursor = new UsnJournalCursor(1UL, 999L);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "D", new UsnJournalCursor(1UL, 100L));
            BrokerProtocol.WriteWarning(response, "D",
                "Catch-up after scan failed: journal wrapped; watching from the current journal position, " +
                "changes made during the scan were not replayed");
            BrokerProtocol.WriteJournalBatch(response, "D", freshCursor, Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeMinimalFakeClient(clientSide);
        var result = await client.ArmScanAndCatchUpAsync(DriveD, CreateOptions());
        await brokerTask;

        Assert.IsTrue(result.Warnings.ContainsKey("D"));
        StringAssert.Contains(result.Warnings["D"], "journal wrapped");
        Assert.IsFalse(result.Errors.ContainsKey("D"));
        Assert.IsTrue(result.AdvancedCursors.ContainsKey("D"));
        Assert.AreEqual(freshCursor, result.AdvancedCursors["D"]);

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
            new BrokerScanOptions { BlockTargets = CreateTargets(), Profile = BrokerScanProfile.Full }, CancellationToken.None);
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
            new BrokerScanOptions { BlockTargets = CreateTargets(), Profile = BrokerScanProfile.DirectoryIndex, KeepFileNames = KeepFileNamesGit });
        await brokerTask;

        Assert.IsTrue(result.Errors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // ---------------------------------------------------------------------------

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
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

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

        await client.StopLiveWatchAsync();
        Assert.IsFalse(client.LastStopTimedOut, "StopLiveWatchAsync must complete via the EndWatchAck handshake, not the timeout fallback.");

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
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

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

        await client.StopLiveWatchAsync();
        Assert.IsFalse(client.LastStopTimedOut, "StopLiveWatchAsync must complete via the EndWatchAck handshake despite the stray batch, not the timeout fallback.");

        await brokerTask;

        // The client is healthy for restart after draining the stray batch.
        await client.SendStartWatchAsync(cursors);
        var restartFrame = await ReadOneFrameAsync(serverSide);
        Assert.AreEqual(BrokerFrameKind.StartWatch, restartFrame.Kind);

        await client.DisposeAsync();
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

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => client.ArmScanAndCatchUpAsync(DriveC, CreateOptions()));

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

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => client.ArmScanAndCatchUpAsync(DriveC, CreateOptions()));

        await client.DisposeAsync();
    }
}
