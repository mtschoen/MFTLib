using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
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
        var host = new JournalBrokerHost(new ScriptedVolumeAccess
        {
            CursorQuery = _ =>
            {
                cursorArmed = true;
                return ArmedCursor;
            },
            JournalRead = (_, cursor, maximumBufferReads) =>
            {
                Assert.IsTrue(capturedBlockWriter.Block.Header.IsComplete);
                Assert.AreEqual(BrokerLiveness.CatchUpBufferReadsPerCall, maximumBufferReads);
                return cursor == ArmedCursor
                    ? ([], cursor with { NextUsn = 12500 })
                    : (Array.Empty<UsnJournalEntry>(), cursor);
            },
            RecordBatches = (drive, _, _, _, _, _) =>
            {
                Assert.IsTrue(cursorArmed);
                Assert.AreEqual(new DriveKey('C'), drive);
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]];
            }
        });

        var frames = await ScanAsync(host, blockWriter);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(ArmedCursor, frames[0].Cursor);
        Assert.ContainsSingle(frame => frame.Kind == BrokerFrameKind.ScanReady, frames);
        Assert.AreEqual(21u, blockWriter.Block.Header.RowCount);
        Assert.AreEqual(18u, blockWriter.Block.Header.NamePoolUsed);
        Assert.AreEqual("section-C", blockWriter.LastSectionName);
        Assert.IsTrue(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, blockWriter.Block.Header.ProducerKind);
        Assert.AreEqual(5u, blockWriter.Block.Header.RootRow);
        Assert.AreEqual(ArmedCursor.JournalIdentifier, blockWriter.Block.Header.UsnJournalIdentifier);
        Assert.AreEqual(ArmedCursor.NextUsn, blockWriter.Block.Header.UsnNextUsn);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(blockWriter.Block, 20).ToString());
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(default(UsnJournalCursor), frames[^1].Cursor);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.Error, frames);
    }

    [TestMethod]
    public async Task ScanChannel_BlockFormat_ForwardsRetainedFileNamesToSectionWriter()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });

        var frames = await ScanAsync(host, blockWriter, [".git"]);

        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.Error, frames);
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual("section-C", blockWriter.LastSectionName);
        Assert.IsNotNull(blockWriter.LastRetainedFileNames);
        Assert.AreSequenceEqual([".git"], [.. blockWriter.LastRetainedFileNames]);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, blockWriter.Block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.None, blockWriter.Block.Rows[20].Flags);
    }

    [TestMethod]
    public async Task ScanChannel_RecordsTheSourceOmitted_AreAddedToTheSkippedRecordCount()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, recordsOmitted, _) =>
            {
                recordsOmitted(3);
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt"), BrokerHostFactory.Record(uint.MaxValue + 1UL, "wide")]];
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });

        var frames = await ScanAsync(host, blockWriter);

        Assert.AreEqual(4L, frames.Single(frame => frame.Kind == BrokerFrameKind.ScanReady).SkippedRecordCount,
            "three left out by the source and one the writer could not place");
    }

    [TestMethod]
    public async Task ScanChannel_BlockFormatWithoutSectionWriterReportsNamedError()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });

        var frames = await ScanAsync(host, null);

        Assert.Contains("blockSectionWriter",
frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message!);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanReady, frames);
    }

    [TestMethod]
    public async Task ScanChannel_BlockSourceFailureLeavesIncompleteBlockAndEmitsNoScanReady()
    {
        using var blockWriter = new RecordingBlockSectionWriter();

        static IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            yield return [BrokerHostFactory.Record(5, ".", 3)];
            throw new IOException("record batch failed");
        }

        var frames = await ScanAsync(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => Batches(),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        }), blockWriter);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.AreEqual(0UL, blockWriter.Block.Header.UsnJournalIdentifier);
        Assert.AreEqual("record batch failed", frames.Single(frame => frame.Kind == BrokerFrameKind.Error).Message);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanReady, frames);
    }

    [TestMethod]
    public async Task ScanChannel_BlockProgressReportsParsingThenTransferring()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var parsingReported = new TestGate();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, reporting, _, _, _) =>
            {
                reporting.Progress!.Report(new BlockWriteProgress(500, 0, 1000, null, BlockScanPhase.Parsing));
                parsingReported.MarkEntered();
                parsingReported.WaitForRelease();
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]];
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
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
        Assert.IsNotEmpty(transfers);
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
            yield return [BrokerHostFactory.Record(5, ".", 3)];
            scanParked.MarkEntered();
            if (!cancellationToken.WaitHandle.WaitOne(HangGuard * 3))
            {
                throw new TimeoutException("The scan was never cancelled.");
            }

            yield return [BrokerHostFactory.Record(20, "file.txt")];
        }

        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, cancellationToken) => Batches(cancellationToken),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        });
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C', "section-C");
        await scanParked.Entered.WaitAsync(HangGuard * 3, TestContext.CancellationToken);

        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HangGuard * 3, TestContext.CancellationToken);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(6u, blockWriter.Block.Header.RowCount);
        Assert.IsFalse(blockWriter.Block.Header.IsComplete);
        Assert.DoesNotContain(frame => frame.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.Error, frames);
    }

    [TestMethod]
    public async Task ScanChannel_ForwardsSharedScanArgumentsAndProducesExpectedRows()
    {
        MftRecord[][] batches =
        [
            [BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(10, "folder", 3),
                new MftRecord(20, 10, new MftRecordFields(1), "file.txt"), BrokerHostFactory.Record(21, "")],
            [BrokerHostFactory.Record(30, "other.txt"), new MftRecord(1UL << 33, 5, new MftRecordFields(1), "huge")]
        ];
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        using var hostWriter = new RecordingBlockSectionWriter(timeProvider: timeProvider);
        var hostFrames = await ScanAsync(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            ScanDrive = (_, _, _, _, _, _) => batches,
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        }), hostWriter);
        var skippedByHost = hostFrames.Single(frame => frame.Kind == BrokerFrameKind.ScanReady).SkippedRecordCount;

        using var directBlock = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"mft-block-direct-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 256,
            NamePoolCapacity = 4096,
            DeleteOnClose = true
        });
        var result = MftBlockScan.WriteToBlock(directBlock, new BlockStamp(ArmedCursor, timeProvider), batches,
            new MftRowSelection(new MftScanOptions()), default, CancellationToken.None);

        Assert.AreEqual(skippedByHost, result.SkippedRecordCount);
        Assert.AreEqual(2L, result.SkippedRecordCount);
        Assert.AreSequenceEqual(BlockBytes(hostWriter.Block), BlockBytes(directBlock));
        await AssertExpectedRowsAsync(hostWriter.Block);
        await AssertExpectedRowsAsync(directBlock);
    }

    static async Task AssertExpectedRowsAsync(BlockFile block)
    {
        Assert.AreEqual(4u, block.Header.LiveRowCount);
        Assert.AreEqual(31u, block.Header.RowCount);
        Assert.AreEqual(RowFlags.None, block.Rows[21].Flags);
        var rootPath = Path.Combine(Path.GetTempPath(), "scan-oracle");
        DriveBlock[] snapshot = [new DriveBlock('C', 0, block, rootPath)];
        try
        {
            (string RelativePath, uint Row, string Name, uint Parent, RowFlags Flags)[] expected =
            [
                ("", 5, ".", 5, RowFlags.InUse | RowFlags.Directory),
                ("folder", 10, "folder", 5, RowFlags.InUse | RowFlags.Directory),
                ("folder/file.txt", 20, "file.txt", 10, RowFlags.InUse),
                ("other.txt", 30, "other.txt", 5, RowFlags.InUse)
            ];
            foreach (var row in expected)
            {
                var entry = LookupEngine.Find(snapshot, Path.Combine(rootPath, row.RelativePath));
                Assert.IsNotNull(entry, $"Expected lookup for {row.RelativePath}");
                Assert.AreEqual(row.Row, entry.Value.RowIndex);
                Assert.AreEqual(row.Name, entry.Value.Name);
                Assert.AreEqual(row.Parent, entry.Value.Row.ParentRow);
                Assert.AreEqual(row.Flags, entry.Value.Row.Flags);
            }

            Assert.IsNull(LookupEngine.Find(snapshot, Path.Combine(rootPath, "huge")));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }


    static byte[] BlockBytes(BlockFile block)
    {
        var header = block.Header;
        return
        [
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BlockHeader>(in header)),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.Rows),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.SequenceNumbers),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.NamePoolCharacters)
        ];
    }

    static async Task<List<BrokerFrame>> ScanAsync(JournalBrokerHost host, IBlockSectionWriter? blockWriter,
        IReadOnlyCollection<string>? retainedFileNames = null)
    {
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenScanChannelAsync('C', "section-C", retainedFileNames);
        return await HostChannelHarness.ReadToEndAsync(pipe);
    }

    public TestContext TestContext { get; set; } = null!;
}
