using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
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
            scanDrive: (_, _, operation, progress, _, _) =>
            {
                operation.Processing("MFT parse");
                progress?.Report(new BlockWriteProgress(2, 0, 2, null, BrokerScanPhase.Parsing));
                return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
            },
            readJournal: (_, since, _) =>
            {
                caughtUpFrom ??= since;
                var tip = new UsnJournalCursor(7, 1500);
                return since == tip ? ([], since) : ([JournalEntries.Create(20, 1200, "file.txt")], tip);
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
        Assert.AreEqual(21u, sectionWriter.Block.Header.RowCount);
        Assert.AreEqual(sectionWriter.Block.Header.NamePoolUsed, finalProgress.BytesProcessed);
        Assert.AreEqual(0L, frames[^2].SkippedRecordCount);
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(1500L, frames[^1].Cursor.NextUsn);
        Assert.AreEqual(0, frames[^1].Entries.Length, "Catch-up entries stay on the host.");
        Assert.AreEqual(Armed, caughtUpFrom);
        Assert.AreEqual("section-C", sectionWriter.LastSectionName);
        Assert.IsTrue(frames.All(frame => frame.Drive == null));
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
        Assert.AreEqual(4096L, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(32768L, loss.JournalSettings.MaximumSize);
        Assert.AreEqual(4000L, loss.BytesBehind);
        Assert.AreEqual(12288L, loss.SizeThatWouldHaveRetained);
        Assert.IsNull(frames[^1].Message, "The failure text stays on the host.");
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
            or BrokerFrameKind.ScanCompleted));
    }

    [TestMethod]
    public async Task ScanChannel_AllowanceReachesSource()
    {
        int? allowance = null;
        var host = CreateHost(processorCount: 8, scanDrive: (_, parseThreads, _, _, _, _) =>
        {
            allowance = parseThreads.Count;
            return [[Record(5, ".", 3)]];
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(8, allowance);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);
        var tip = new UsnJournalCursor(7, 1500);
        var host = CreateHost(readJournal: (_, _, _) => ([JournalEntries.Create(20, 1200, "file.txt")], tip));
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        // The source broke its contract: a read from the tip returned entries again. Shipping them
        // would duplicate the first read's entries, so the scan fails loudly instead.
        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        StringAssert.Contains(frames[^1].Message, "without advancing");
    }

    static async Task<List<BrokerFrame>> ScanWithFailedCatchUpAsync(Exception failure, Action? onCursorQuery = null)
    {
        var host = CreateHost(
            queryCursor: _ =>
            {
                onCursorQuery?.Invoke();
                return Armed;
            },
            readJournal: (_, _, _) => throw failure);
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        return await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));
    }

    static void AssertScanReadyThen(List<BrokerFrame> frames, BrokerFrameKind terminal)
    {
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(terminal, frames[^1].Kind);
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.ScanCompleted));
    }
}
