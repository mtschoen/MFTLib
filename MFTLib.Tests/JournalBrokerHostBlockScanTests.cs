using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostBlockScanTests
{
    static readonly UsnJournalCursor ArmedCursor = new(71, 12345);

    [TestMethod]
    public async Task ScanChannel_BlockFormatWritesRowsAndArmedCursorWithoutPayload()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var capturedBlockWriter = new BlockWriter(blockWriter.Block);
        var cursorArmed = false;
        var host = new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                _ =>
                {
                    cursorArmed = true;
                    return ArmedCursor;
                },
                ReadJournal: (_, cursor, maximumBufferReads) =>
                {
                    Assert.IsTrue(capturedBlockWriter.Block.Header.IsComplete);
                    Assert.AreEqual(BrokerLiveness.CatchUpBufferReadsPerCall, maximumBufferReads);
                    return cursor == ArmedCursor
                        ? (Array.Empty<UsnJournalEntry>(), new UsnJournalCursor(cursor.JournalId, 12500))
                        : (Array.Empty<UsnJournalEntry>(), cursor);
                },
                ScanDrive: (driveLetter, _, _, _, _) =>
                {
                    Assert.IsTrue(cursorArmed);
                    Assert.AreEqual("C", driveLetter);
                    return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
                }));

        var frames = await ScanAsync(host, blockWriter);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(ArmedCursor, frames[0].Cursor);
        Assert.AreEqual(1, frames.Count(frame => frame.Kind == BrokerFrameKind.ScanReady));
        Assert.AreEqual(21u, blockWriter.Block.Header.RowCount);
        Assert.AreEqual(18u, blockWriter.Block.Header.NamePoolUsed);
        Assert.AreEqual("section-C", blockWriter.LastSectionName);
        Assert.IsTrue(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, blockWriter.Block.Header.ProducerKind);
        Assert.AreEqual(5u, blockWriter.Block.Header.RootRow);
        Assert.AreEqual(ArmedCursor.JournalId, blockWriter.Block.Header.UsnJournalId);
        Assert.AreEqual(ArmedCursor.NextUsn, blockWriter.Block.Header.UsnNextUsn);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(blockWriter.Block, 20).ToString());
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(12500L, frames[^1].Cursor.NextUsn);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error));
    }

    [TestMethod]
    public async Task ScanChannel_BlockFormat_ForwardsDirectoryIndexProfileAndKeepNamesToSectionWriter()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = CreateHost((_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]);

        var frames = await ScanAsync(host, blockWriter, BrokerScanProfile.DirectoryIndex, [".git"]);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error));
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual("section-C", blockWriter.LastSectionName);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, blockWriter.LastFilter.Profile);
        Assert.IsNotNull(blockWriter.LastFilter.KeepFileNames);
        CollectionAssert.AreEqual(new[] { ".git" }, blockWriter.LastFilter.KeepFileNames.ToArray());
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, blockWriter.Block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.None, blockWriter.Block.Rows[20].Flags);
    }

    [TestMethod]
    public async Task ScanChannel_BlockFormatWithoutSectionWriterReportsNamedError()
    {
        var host = CreateHost((_, _, _, _, _) => [[Record(5, ".", 3)]]);

        var frames = await ScanAsync(host, null);

        StringAssert.Contains(frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message,
            "blockSectionWriter");
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.ScanReady));
    }

    [TestMethod]
    public async Task ScanChannel_BlockSourceFailureLeavesIncompleteBlockAndEmitsNoScanReady()
    {
        using var blockWriter = new RecordingBlockSectionWriter();

        static IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            yield return [Record(5, ".", 3)];
            throw new IOException("record batch failed");
        }

        var frames = await ScanAsync(CreateHost((_, _, _, _, _) => Batches()), blockWriter);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(0UL, blockWriter.Block.Header.UsnJournalId);
        Assert.AreEqual("record batch failed", frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.ScanReady));
    }

    [TestMethod]
    public async Task ScanChannel_BlockProgressReportsParsingThenTransferring()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var parsingReported = new TestGate();
        var host = CreateHost((_, _, _, progress, _) =>
        {
            progress!.Report(new BlockWriteProgress(500, 0, 1000, null, BrokerScanPhase.Parsing));
            parsingReported.MarkEntered();
            parsingReported.WaitForRelease();
            return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
        });
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C', "section-C");

        await HostChannelHarness.ReadFrameAsync(pipe); // Cursor
        var firstProgress = (await HostChannelHarness.ReadFrameAsync(pipe))!.Value;
        Assert.AreEqual(BrokerScanPhase.Parsing, firstProgress.Progress?.Phase);
        Assert.AreEqual(500L, firstProgress.Progress?.RecordsProcessed);
        Assert.AreEqual(1000L, firstProgress.Progress?.TotalRecords);
        parsingReported.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var transfers = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress).ToArray();
        Assert.IsTrue(transfers.Length > 0);
        Assert.IsTrue(transfers.All(frame => frame.Progress?.Phase == BrokerScanPhase.Transferring));
        Assert.AreEqual(18L, transfers[^1].Progress?.BytesProcessed);
        Assert.AreEqual(transfers[^1].Progress?.BytesProcessed, transfers[^1].Progress?.TotalBytes);
        Assert.AreEqual(1000L, transfers[^1].Progress?.TotalRecords);
    }

    [TestMethod]
    public async Task ScanChannel_CancelledBlockScanLeavesIncompleteBlockWithoutErrorOrScanReady()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var scanParked = new TestGate();

        // The second batch is yielded only once the session has ended, so the writer meets a
        // cancelled token before it can write it.
        IEnumerable<IReadOnlyList<MftRecord>> Batches(CancellationToken cancellationToken)
        {
            yield return [Record(5, ".", 3)];
            scanParked.MarkEntered();
            cancellationToken.WaitHandle.WaitOne(HostChannelHarness.HangGuard);
            yield return [Record(20, "file.txt")];
        }

        var host = CreateHost((_, _, _, _, cancellationToken) => Batches(cancellationToken));
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C', "section-C");
        await scanParked.Entered.WaitAsync(HostChannelHarness.HangGuard);

        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.Error));
    }

    static JournalBrokerHost CreateHost(MftRecordBatchSource source)
    {
        return new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(_ => ArmedCursor, source, (_, cursor, _) => ([], cursor)));
    }

    // Scans drive C into a section named "section-C" and returns every frame the drive pipe carried.
    static async Task<List<BrokerFrame>> ScanAsync(JournalBrokerHost host, IBlockSectionWriter? blockWriter,
        BrokerScanProfile profile = BrokerScanProfile.Full, IReadOnlyCollection<string>? keepFileNames = null)
    {
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C', "section-C", profile, keepFileNames);
        return await HostChannelHarness.ReadToEndAsync(pipe);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }
}
