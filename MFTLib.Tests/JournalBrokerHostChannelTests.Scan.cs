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
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, reporting, _, _, _) =>
            {
                reporting.Operation!.Processing("MFT parse");
                reporting.Progress?.Report(new BlockWriteProgress(2, 0, 2, null, BlockScanPhase.Parsing));
                return [[BrokerHostFactory.Record(5, ".", 3)], [BrokerHostFactory.Record(20, "file.txt")]];
            },
            ReadJournal = (_, since, _) =>
            {
                caughtUpFrom ??= since;
                var tip = new UsnJournalCursor(7, 1500);
                return since == tip ? ([], since) : ([JournalEntries.Create(20, 1200, "file.txt")], tip);
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, sectionWriter);

        var pipe = await harness.OpenScanChannelAsync('C', "section-C");
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(Armed, frames[0].Cursor);
        Assert.IsTrue(frames.Skip(1).Take(frames.Count - 3).All(frame => frame.Kind == BrokerFrameKind.ScanProgress));
        Assert.IsGreaterThan(3, frames.Count, "At least the final progress sample precedes ScanReady.");
        var finalProgress = frames[^3].Progress!.Value;
        Assert.AreEqual(BrokerScanPhase.Transferring, finalProgress.Phase);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(21u, sectionWriter.Block.Header.RowCount);
        Assert.AreEqual(sectionWriter.Block.Header.NamePoolUsed, finalProgress.BytesProcessed);
        Assert.AreEqual(0L, frames[^2].SkippedRecordCount);
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(default(UsnJournalCursor), frames[^1].Cursor);
        Assert.IsEmpty(frames[^1].Entries, "Catch-up entries stay on the host.");
        Assert.AreEqual(Armed, caughtUpFrom);
        Assert.AreEqual("section-C", sectionWriter.LastSectionName);
        Assert.IsTrue(frames.All(frame => frame.Drive == null));
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalProvesLoss_EmitsScanReadyThenCatchUpLostAndCloses()
    {
        var cursorQueries = 0;
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"),
            () => Interlocked.Increment(ref cursorQueries));

        AssertScanReadyThen(frames, BrokerFrameKind.CatchUpLost);
        var loss = frames[^1].RequireCatchUpLoss('C');
        Assert.AreEqual('C', loss.Drive);
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
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 500, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalCannotAnswer_EmitsErrorAfterScanReady()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);
        var frames = await ScanWithFailedCatchUpAsync(new IOException("catch-up read failed"));

        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.AreEqual("catch-up read failed", frames[^1].Message);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpFailsAndJournalRecreated_CatchUpLostHasNoSize()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(8, 5000, 9000, 4096, 32768));
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
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 5000, 9000, 4096, 32768));
        var frames = await ScanWithFailedCatchUpAsync(new OperationCanceledException());

        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^1].Kind);
        Assert.DoesNotContain(frame => frame.Kind is BrokerFrameKind.CatchUpLost or BrokerFrameKind.Error
            or BrokerFrameKind.ScanCompleted, frames);
    }

    [TestMethod]
    public async Task ScanChannel_AllowanceReachesSource()
    {
        int? allowance = null;
        var host = BrokerHostFactory.Create(new()
        {
            ProcessorCount = 8,
            ScanDrive = (_, parseThreads, _, _, _, _) =>
            {
                allowance = parseThreads.Count;
                return [[BrokerHostFactory.Record(5, ".", 3)]];
            },
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(8, allowance);
    }

    [TestMethod]
    public async Task ScanChannel_CatchUpReturnsEntriesWithoutAdvancing_WritesErrorAndNoDuplicateBatch()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);
        var tip = new UsnJournalCursor(7, 1500);
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            ReadJournal = (_, _, _) => ([JournalEntries.Create(20, 1200, "file.txt")], tip),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        // The source broke its contract: a read from the tip returned entries again. Shipping them
        // would duplicate the first read's entries, so the scan fails loudly instead.
        AssertScanReadyThen(frames, BrokerFrameKind.Error);
        Assert.Contains("without advancing", frames[^1].Message!);
    }

    static async Task<List<BrokerFrame>> ScanWithFailedCatchUpAsync(Exception failure, Action? onCursorQuery = null)
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ =>
            {
                onCursorQuery?.Invoke();
                return Armed;
            },
            ReadJournal = (_, _, _) => throw failure,
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        return await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));
    }

    static void AssertScanReadyThen(List<BrokerFrame> frames, BrokerFrameKind terminal)
    {
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(terminal, frames[^1].Kind);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanCompleted, frames);
    }
}
