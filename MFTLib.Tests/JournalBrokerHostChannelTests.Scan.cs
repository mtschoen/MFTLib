using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostChannelTests
{
    [TestMethod]
    public async Task ScanChannel_EmitsCursorProgressReadyAndCatchUpInOrderThenCloses()
    {
        using var sectionWriter = new RecordingBlockSectionWriter();
        UsnJournalCursor? caughtUpFrom = null;
        var host = CreateHost(
            scanDrive: (_, _, operation, progress, _) =>
            {
                operation.Processing("MFT parse");
                progress?.Report(new BlockWriteProgress(2, 0, 2, null, BrokerScanPhase.Parsing));
                return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
            },
            readJournal: (_, since) =>
            {
                caughtUpFrom = since;
                return ([JournalEntryFactory.Create(20, 1200, "file.txt")], new UsnJournalCursor(7, 1500));
            });
        await using var harness = new HostChannelHarness(host, sectionWriter);

        var pipe = await harness.OpenScanChannelAsync('C', "section-C");
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(Armed, frames[0].Cursor);
        Assert.IsTrue(frames.Skip(1).Take(frames.Count - 3).All(frame => frame.Kind == BrokerFrameKind.ScanProgress));
        Assert.IsTrue(frames.Count > 3, "At least the final progress sample precedes ScanReady.");
        var finalProgress = frames[^3].Progress!.Value;
        Assert.AreEqual(BrokerScanPhase.Transferring, finalProgress.Phase);
        Assert.AreEqual(string.Empty, finalProgress.DriveLetter, "The drive belongs to the channel, not the frame.");
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(21L, frames[^2].RowCount);
        Assert.AreEqual(sectionWriter.Block.Header.NamePoolUsed, frames[^2].NamePoolUsedBytes);
        Assert.AreEqual(finalProgress.BytesProcessed, frames[^2].NamePoolUsedBytes);
        Assert.AreEqual(0L, frames[^2].SkippedRecordCount);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind);
        Assert.AreEqual(1500L, frames[^1].Cursor.NextUsn);
        Assert.AreEqual("file.txt", frames[^1].Entries.Single().FileName);
        Assert.AreEqual(Armed, caughtUpFrom);
        Assert.AreEqual("section-C", sectionWriter.LastSectionName);
        Assert.IsTrue(frames.All(frame => frame.Drive == null));
    }

    [TestMethod]
    public async Task ScanChannel_ForwardsProfileAndKeepNamesToSectionWriter()
    {
        using var sectionWriter = new RecordingBlockSectionWriter();
        var host = CreateHost(scanDrive: (_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]);
        await using var harness = new HostChannelHarness(host, sectionWriter);

        var pipe = await harness.OpenScanChannelAsync('C', "section-C", BrokerScanProfile.DirectoryIndex, [".git"]);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, sectionWriter.LastFilter.Profile);
        CollectionAssert.AreEqual(new[] { ".git" }, sectionWriter.LastFilter.KeepFileNames!.ToArray());
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalProvesLoss_EmitsScanReadyThenCatchUpLostAndCloses()
    {
        var cursorQueries = 0;
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(7, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"),
            () => Interlocked.Increment(ref cursorQueries));

        AssertScanReadyThen(frames, BrokerFrameKind.CatchUpLost);
        var loss = frames[^1].RequireCatchUpLoss('C');
        Assert.AreEqual('C', loss.DriveLetter);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, loss.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(Armed.NextUsn, loss.CheckpointUsn);
        Assert.AreEqual(5000L, loss.FirstUsn);
        Assert.AreEqual(9000L, loss.NextUsn);
        Assert.AreEqual(4096L, loss.AllocationDelta);
        Assert.AreEqual(32768L, loss.MaximumSize);
        Assert.AreEqual(4000L, loss.BytesBehind);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
        Assert.AreEqual(1, cursorQueries, "The host must not query a fresh cursor after a failed catch-up.");
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndCursorStillRetained_EmitsErrorAfterScanReady()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(7, 500, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalCannotAnswer_EmitsErrorAfterScanReady()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalRecreated_CatchUpLostHasNoSize()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(8, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.CatchUpLost);
        var loss = frames[^1].RequireCatchUpLoss('C');
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpCancelled_WritesNoCatchUpLost()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(7, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new OperationCanceledException());

        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^1].Kind);
        Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.CatchUpLost or BrokerFrameKind.Error
            or BrokerFrameKind.JournalBatch));
    }

    [TestMethod]
    public async Task ScanChannel_AllowanceReachesSource()
    {
        int? allowance = null;
        var host = CreateHost(processorCount: 8, scanDrive: (_, parseThreads, _, _, _) =>
        {
            allowance = parseThreads.Count;
            return [[Record(5, ".", 3)]];
        });
        await using var harness = new HostChannelHarness(host, new EnumeratingSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind);
        Assert.AreEqual(8, allowance);
    }

    static async Task<List<BrokerFrame>> ScanWithFailedCatchUpAsync(Exception failure, Action? onCursorQuery = null)
    {
        var host = CreateHost(
            queryCursor: _ =>
            {
                onCursorQuery?.Invoke();
                return Armed;
            },
            readJournal: (_, _) => throw failure);
        await using var harness = new HostChannelHarness(host, new EnumeratingSectionWriter());
        return await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));
    }

    static void AssertScanReadyThen(List<BrokerFrame> frames, BrokerFrameKind terminal)
    {
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(terminal, frames[^1].Kind);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.JournalBatch));
    }

    // Consumes every batch without a block, for scans whose block content is not under test.
    sealed class EnumeratingSectionWriter : IBlockSectionWriter
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
