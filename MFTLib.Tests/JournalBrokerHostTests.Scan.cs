using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public void CatchUp_DelegatesToReadJournal_ReturnsAdvancedCursor()
    {
        var since = new UsnJournalCursor(7UL, 100L);
        var advanced = new UsnJournalCursor(7UL, 250L);
        var batch = new[] { JournalEntryFactory.Create(1, 110, "a") };
        var host = ScanHost(readJournal: (drive, cursor) =>
        {
            Assert.AreEqual("C:", drive);
            Assert.AreEqual(since, cursor);
            return (batch, advanced);
        });

        var (entries, updated) = host.CatchUp("C:", since);

        Assert.AreSame(batch, entries);
        Assert.AreEqual(advanced, updated);
    }

    [TestMethod]
    public async Task ArmAndScan_EmitsCursorScanReadyAndCatchUp()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(
            scanDrive: (_, _, _, _, _) => [[ScanRecord(100, "a.txt")]],
            readJournal: (_, cursor) => ([ScanEntry()], new UsnJournalCursor(cursor.JournalId, cursor.NextUsn + 1)));
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness, 'C', "mftlib-scan-C");

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(ScanArmedCursor, frames[0].Cursor);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        var scanReady = frames.Single(f => f.Kind == BrokerFrameKind.ScanReady);
        var journalBatch = frames.Single(f => f.Kind == BrokerFrameKind.JournalBatch);
        Assert.AreEqual(101L, scanReady.RowCount);
        Assert.AreEqual(1, journalBatch.Entries.Length);
        Assert.AreEqual(1, InUseRowCount(blockWriter));
        Assert.AreEqual("mftlib-scan-C", blockWriter.LastSectionName);
        Assert.IsTrue(frames.All(f => f.Drive == null && f.RequestId == 0), "A drive pipe carries no drive and no request id.");
    }

    [TestMethod]
    public async Task MftRecordBatchSource_StreamsBatchesToBlockSectionWriter()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 0L),
            scanDrive: (_, _, _, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "batch1.txt", null)],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "batch2.txt", null)]
            ]);
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness, 'C', "mftlib-streaming-C");

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        Assert.AreEqual(3L, frames.Single(f => f.Kind == BrokerFrameKind.ScanReady).RowCount);
        Assert.AreEqual("batch1.txt", NamePool.ReadRowName(blockWriter.Block, 1).ToString());
        Assert.AreEqual("batch2.txt", NamePool.ReadRowName(blockWriter.Block, 2).ToString());
        Assert.AreEqual(2, InUseRowCount(blockWriter));
    }

    [TestMethod]
    public async Task DirectoryIndexProfile_KeepFileNameMatch_KeepsTheNamedFile()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, ScanKeepFileNamesGit);

        Assert.AreEqual(2, InUseRowCount(writer)); // repo (directory) + .git (named match)
    }

    [TestMethod]
    public async Task DirectoryIndexProfile_KeepFileNameMatch_IsCaseInsensitive()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, ScanKeepFileNamesGitUppercase);

        Assert.AreEqual(2, InUseRowCount(writer)); // repo (directory) + .git (matched despite case)
    }

    [TestMethod]
    public async Task DirectoryIndexProfile_NonMatchingFiles_AreDropped()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, ScanKeepFileNamesNonMatching);

        Assert.AreEqual(1, InUseRowCount(writer)); // repo (directory) only
    }

    [TestMethod]
    public async Task DirectoryIndexProfile_NullKeepFileNames_YieldsDirectoriesOnly()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, null);

        Assert.AreEqual(1, InUseRowCount(writer)); // repo (directory) only
    }

    [TestMethod]
    public async Task DirectoryIndexProfile_EmptyKeepFileNames_YieldsDirectoriesOnly()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, Array.Empty<string>());

        Assert.AreEqual(1, InUseRowCount(writer)); // repo (directory) only
    }

    [TestMethod]
    public async Task ArmAndScan_UnknownProfile_WritesMalformedErrorAndSessionContinues()
    {
        var host = ScanHost(queryVolumeInfo: _ => ControlVolume);
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe,
            writer => BrokerProtocol.WriteArmAndScan(writer, "section", (BrokerScanProfile)99));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        StringAssert.Contains(frames[0].Message, "malformed");
        StringAssert.Contains(frames[0].Message, "99");
        Assert.IsNull(blockWriter.LastSectionName, "No scan runs for a request the host could not decode.");
        await AssertControlStillAnswersAsync(harness);
    }

    [TestMethod]
    public async Task DriveFailure_EmitsErrorFrameAndSessionContinues()
    {
        var host = ScanHost(
            queryCursor: _ => throw new InvalidOperationException("journal wrapped"),
            queryVolumeInfo: _ => ControlVolume);
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness, 'D', "mftlib-scan-D");

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual(0u, frames[0].RequestId);
        Assert.AreEqual("journal wrapped", frames[0].Message);
        await AssertControlStillAnswersAsync(harness);
    }

    [TestMethod]
    public async Task CatchUpThrows_EmitsErrorAfterScanReady_OtherDriveUnaffected()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);
        var host = ScanHost(
            queryCursor: drive => drive == "C" ? new UsnJournalCursor(1UL, 100L) : new UsnJournalCursor(2UL, 200L),
            readJournal: (drive, since) => drive == "C"
                ? throw new InvalidOperationException("journal wrapped")
                : (Array.Empty<UsnJournalEntry>(), since));
        await using var harness = new HostChannelHarness(host, new RowCountingSectionWriter());

        var failing = await harness.OpenScanChannelAsync('C');
        var healthy = await harness.OpenScanChannelAsync('D');
        var failingFrames = await HostChannelHarness.ReadToEndAsync(failing);
        var healthyFrames = await HostChannelHarness.ReadToEndAsync(healthy);

        Assert.AreEqual(BrokerFrameKind.Cursor, failingFrames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, failingFrames[^2].Kind);
        Assert.AreEqual(BrokerFrameKind.Error, failingFrames[^1].Kind);
        Assert.AreEqual("journal wrapped", failingFrames[^1].Message);
        Assert.IsFalse(failingFrames.Any(f => f.Kind == BrokerFrameKind.JournalBatch),
            "A failed catch-up ships no batch that would let the client treat the scan as caught up.");

        Assert.AreEqual(BrokerFrameKind.JournalBatch, healthyFrames[^1].Kind);
        Assert.AreEqual(new UsnJournalCursor(2UL, 200L), healthyFrames[0].Cursor);
        Assert.IsFalse(healthyFrames.Any(f => f.Kind == BrokerFrameKind.Error));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void RealBlockSectionWriter_WritesBlock_ClientCanReadItBack()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var sectionName = NamedBlockSection.BuildSectionName('C');
        var (block, lifetime) = NamedBlockSection.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"broker-block-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 128,
            NamePoolCapacity = 1024,
            DeleteOnClose = true
        }, sectionName);
        using (block)
        using (lifetime)
        {
            var cursor = new UsnJournalCursor(7, 100);
            var written = new RealBlockSectionWriter().Write(sectionName, cursor,
                [[new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                    new MftRecord(100, 5, new MftRecordFields(1, FileAttributes.Normal, 2048), "nöte.txt", null)]],
                MftBlockRowFilter.Full, null, CancellationToken.None);

            Assert.AreEqual(101L, written.RowCount);
            Assert.AreEqual(18L, written.NamePoolUsedBytes);
            Assert.AreEqual(0L, written.SkippedRecordCount);
            Assert.IsTrue(block.Header.IsComplete);
            Assert.AreEqual(cursor.JournalId, block.Header.UsnJournalId);
            Assert.AreEqual(cursor.NextUsn, block.Header.UsnNextUsn);
            Assert.AreEqual("nöte.txt", NamePool.ReadRowName(block, 100).ToString());
            Assert.AreEqual(2048L, block.Rows[100].Size);
        }
    }

    static async Task<RecordingBlockSectionWriter> ServeDirectoryIndexAsync(
        MftRecord[] records, IReadOnlyCollection<string>? keepFileNames)
    {
        var writer = new RecordingBlockSectionWriter();
        var host = ScanHost(scanDrive: (_, _, _, _, _) => [records]);
        await using var harness = new HostChannelHarness(host, writer);

        var frames = await ScanFramesAsync(harness, 'C', "mftlib-scan-C", BrokerScanProfile.DirectoryIndex, keepFileNames);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind);
        return writer;
    }

    // A control request answered after a channel failed proves the session outlived it.
    static async Task AssertControlStillAnswersAsync(HostChannelHarness harness)
    {
        var requestId = harness.NextRequestId();
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, requestId, "C"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, reply.Kind);
        Assert.AreEqual(requestId, reply.RequestId);
    }

    // Counts rows without writing a block, for scans that run on several drives at once (a
    // recording writer owns one block).
    sealed class RowCountingSectionWriter : IBlockSectionWriter
    {
        public BlockWriteResult Write(string sectionName, UsnJournalCursor cursor,
            IEnumerable<IReadOnlyList<MftRecord>> batches, MftBlockRowFilter filter,
            IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
        {
            long rows = 0;
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows += batch.Count;
            }

            return new BlockWriteResult(rows, 0, 0, false);
        }
    }
}
