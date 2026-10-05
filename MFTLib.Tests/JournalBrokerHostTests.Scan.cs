using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task ArmAndScan_EmitsCursorScanReadyAndCatchUp()
    {
        using var blockWriter = new RecordingBlockSectionWriter();
        var host = ScanHost(
            scanDrive: (_, _, _, _, _) => [[ScanRecord(100, "a.txt")]],
            readJournal: CatchUpSources.ToTip(new UsnJournalCursor(ScanArmedCursor.JournalId, ScanArmedCursor.NextUsn + 1),
                ScanEntry()));
        await using var harness = new HostChannelHarness(host, blockWriter);

        var frames = await ScanFramesAsync(harness, 'C', "mftlib-scan-C");

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(ScanArmedCursor, frames[0].Cursor);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        Assert.AreEqual(1, frames.Count(f => f.Kind == BrokerFrameKind.ScanReady));
        var completed = frames.Single(f => f.Kind == BrokerFrameKind.ScanCompleted);
        Assert.AreEqual(101u, blockWriter.Block.Header.RowCount);
        Assert.AreEqual(ScanArmedCursor.NextUsn + 1, completed.Cursor.NextUsn);
        Assert.AreEqual(0, completed.Entries.Length, "Catch-up entries stay on the host.");
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
        Assert.AreEqual(1, frames.Count(f => f.Kind == BrokerFrameKind.ScanReady));
        Assert.AreEqual(3u, blockWriter.Block.Header.RowCount);
        Assert.AreEqual("batch1.txt", NamePool.ReadRowName(blockWriter.Block, 1).ToString());
        Assert.AreEqual("batch2.txt", NamePool.ReadRowName(blockWriter.Block, 2).ToString());
        Assert.AreEqual(2, InUseRowCount(blockWriter));
    }

    // The directory (repo) is always kept; a file is kept only when its name matches a keep name.
    [DataTestMethod]
    [DataRow(new[] { ".git" }, 2, DisplayName = "KeepFileNameMatch_KeepsTheNamedFile")]
    [DataRow(new[] { ".GIT" }, 2, DisplayName = "KeepFileNameMatch_IsCaseInsensitive")]
    [DataRow(new[] { "other.txt" }, 1, DisplayName = "NonMatchingFiles_AreDropped")]
    [DataRow(null, 1, DisplayName = "NullKeepFileNames_YieldsDirectoriesOnly")]
    [DataRow(new string[0], 1, DisplayName = "EmptyKeepFileNames_YieldsDirectoriesOnly")]
    public async Task DirectoryIndexProfile_KeepFileNames_DecideWhichFilesAreKept(string[]? keepFileNames, int expectedInUseRows)
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, keepFileNames);

        Assert.AreEqual(expectedInUseRows, InUseRowCount(writer));
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
            readJournal: (drive, since, _) => drive == "C"
                ? throw new InvalidOperationException("journal wrapped")
                : (Array.Empty<UsnJournalEntry>(), since));
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var failing = await harness.OpenScanChannelAsync('C');
        var healthy = await harness.OpenScanChannelAsync('D');
        var failingFrames = await HostChannelHarness.ReadToEndAsync(failing);
        var healthyFrames = await HostChannelHarness.ReadToEndAsync(healthy);

        Assert.AreEqual(BrokerFrameKind.Cursor, failingFrames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, failingFrames[^2].Kind);
        Assert.AreEqual(BrokerFrameKind.Error, failingFrames[^1].Kind);
        Assert.AreEqual("journal wrapped", failingFrames[^1].Message);
        Assert.IsFalse(failingFrames.Any(f => f.Kind == BrokerFrameKind.ScanCompleted),
            "A failed catch-up ships no batch that would let the client treat the scan as caught up.");

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, healthyFrames[^1].Kind);
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
                MftBlockRowFilter.Full, default, CancellationToken.None);

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

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
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
}
