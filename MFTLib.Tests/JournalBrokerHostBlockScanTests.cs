using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostBlockScanTests
{
    static readonly UsnJournalCursor ArmedCursor = new(71, 12345);

    [TestMethod]
    public async Task ServeOnce_BlockFormatWritesRowsAndArmedCursorWithoutPayload()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var capturedBlockWriter = new BlockWriter(blockWriter.Block);
        var cursorArmed = false;
        var host = new JournalBrokerHost(
            _ =>
            {
                cursorArmed = true;
                return ArmedCursor;
            },
            readJournal: (_, cursor) =>
            {
                Assert.AreEqual(ArmedCursor, cursor);
                Assert.IsTrue(capturedBlockWriter.Block.Header.IsComplete);
                return (Array.Empty<UsnJournalEntry>(), new UsnJournalCursor(cursor.JournalId, 12500));
            },
            scanDrive: (driveLetter, _, _) =>
            {
                Assert.IsTrue(cursorArmed);
                Assert.AreEqual("C", driveLetter);
                return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
            });

        var frames = await ServeAsync(host, blockWriter);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(ArmedCursor, frames[0].Cursor);
        var scanReady = frames.Single(frame => frame.Kind == BrokerFrameKind.ScanReady);
        Assert.AreEqual("section-C", scanReady.MmfName);
        Assert.AreEqual(21L, scanReady.RowCount);
        Assert.AreEqual(18L, scanReady.NamePoolUsedBytes);
        Assert.AreEqual("section-C", blockWriter.LastSectionName);
        Assert.IsTrue(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, blockWriter.Block.Header.ProducerKind);
        Assert.AreEqual(5u, blockWriter.Block.Header.RootRow);
        Assert.AreEqual(ArmedCursor.JournalId, blockWriter.Block.Header.UsnJournalId);
        Assert.AreEqual(ArmedCursor.NextUsn, blockWriter.Block.Header.UsnNextUsn);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(blockWriter.Block, 20).ToString());
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind);
        Assert.AreEqual(12500L, frames[^1].Cursor.NextUsn);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error));
    }

    [TestMethod]
    public async Task ServeOnce_BlockFormat_ForwardsDirectoryIndexProfileAndKeepNamesToSectionWriter()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = CreateHost((_, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]);

        var (client, server) = DuplexStream.CreatePair();
        await using var clientLifetime = client;
        await using var serverLifetime = server;
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:section-C:1", [".git"]);
        await client.WriteAsync(request.WrittenMemory);
        await host.ServeAsync(server, blockWriter, true, default);
        await server.DisposeAsync();
        var frames = await ReadFramesAsync(client);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error));
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, blockWriter.LastFilter.Profile);
        Assert.IsNotNull(blockWriter.LastFilter.KeepFileNames);
        CollectionAssert.AreEqual(new[] { ".git" }, blockWriter.LastFilter.KeepFileNames.ToArray());
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, blockWriter.Block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.None, blockWriter.Block.Rows[20].Flags);
    }

    [TestMethod]
    public async Task ServeOnce_BlockFormatWithoutSectionWriterReportsNamedError()
    {
        var host = CreateHost((_, _, _) => [[Record(5, ".", 3)]]);

        var frames = await ServeAsync(host, null);

        StringAssert.Contains(frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message,
            "blockSectionWriter");
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.ScanReady));
    }

    [TestMethod]
    public async Task ServeOnce_BlockSourceFailureLeavesIncompleteBlockAndEmitsNoScanReady()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        static IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            yield return [Record(5, ".", 3)];
            throw new IOException("record batch failed");
        }

        var frames = await ServeAsync(CreateHost((_, _, _) => Batches()), blockWriter);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(0UL, blockWriter.Block.Header.UsnJournalId);
        Assert.AreEqual("record batch failed", frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.ScanReady));
    }

    static JournalBrokerHost CreateHost(MftRecordBatchSource source)
    {
        return new JournalBrokerHost(_ => ArmedCursor,
            source, (_, cursor) => ([], cursor));
    }

    static async Task<List<BrokerFrame>> ServeAsync(JournalBrokerHost host, IBlockSectionWriter? blockWriter, CancellationToken cancellationToken = default)
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var clientLifetime = client;
        await using var serverLifetime = server;
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:section-C:0");
        await client.WriteAsync(request.WrittenMemory, cancellationToken);
        await host.ServeAsync(server, blockWriter, true, cancellationToken);
        await server.DisposeAsync();
        return await ReadFramesAsync(client);
    }

    static async Task<List<BrokerFrame>> ReadFramesAsync(Stream client)
    {
        using var response = new MemoryStream();
        await client.CopyToAsync(response);
        var bytes = response.ToArray();
        var frames = new List<BrokerFrame>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            frames.Add(BrokerProtocol.ReadFrame(bytes.AsSpan(offset), out var consumed));
            offset += consumed;
        }

        return frames;
    }

    [TestMethod]
    public async Task ServeOnce_BlockProgressReportsParsingThenTransferring()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var allowTransfer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost((_, progress, cancellationToken) =>
        {
            progress?.Report(new BlockWriteProgress(100, 0, 100, null, BrokerScanPhase.Parsing));
            allowTransfer.Task.Wait(cancellationToken);
            return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
        });
        var (client, server) = DuplexStream.CreatePair();
        await using var clientLifetime = client;
        await using var serverLifetime = server;
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:section-C:0");
        await client.WriteAsync(request.WrittenMemory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = host.ServeAsync(server, blockWriter, true, timeout.Token);
        try
        {
            var firstFrame = await ReadFrameAsync(client, timeout.Token);
            Assert.AreEqual(BrokerScanPhase.Parsing, firstFrame.Progress?.Phase);
            Assert.AreEqual(100L, firstFrame.Progress?.RecordsProcessed);
        }
        finally
        {
            allowTransfer.TrySetResult();
            await serving;
        }

        await server.DisposeAsync();
        var frames = await ReadFramesAsync(client);
        var transfers = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress).ToArray();
        Assert.IsTrue(transfers.Length > 0);
        Assert.IsTrue(transfers.All(frame => frame.Progress?.Phase == BrokerScanPhase.Transferring));
        Assert.AreEqual(18L, transfers[^1].Progress?.BytesProcessed);
        Assert.AreEqual(100L, transfers[^1].Progress?.TotalRecords);
    }

    [TestMethod]
    public async Task ServeOnce_CancelledBlockScanLeavesIncompleteBlockWithoutErrorOrScanReady()
    {
        using var cancellation = new CancellationTokenSource();
        using var blockWriter = new RecordingBlockSectionWriter();

        static IEnumerable<IReadOnlyList<MftRecord>> Batches(CancellationTokenSource cancellation)
        {
            yield return [Record(5, ".", 3)];
            cancellation.Cancel();
            yield return [Record(20, "file.txt")];
        }

        var batches = Batches(cancellation);
        var frames = await ServeAsync(CreateHost((_, _, _) => batches), blockWriter, cancellation.Token);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.Error));
    }

    static async Task<BrokerFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var bytes = new byte[sizeof(int) + BinaryPrimitives.ReadInt32LittleEndian(header)];
        header.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(sizeof(int)), cancellationToken);
        return BrokerProtocol.ReadFrame(bytes, out _);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }
}
