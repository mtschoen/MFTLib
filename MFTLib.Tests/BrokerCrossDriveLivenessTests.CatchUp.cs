using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Lost catch-up over the broker (spec 2.6.6, level L1): the host's catch-up source throws, the
// journal window says whether the armed cursor is still retained, and the index's scan operation
// counts, retries and stops. Both clocks stay where they started: nothing here depends on time.
public sealed partial class BrokerCrossDriveLivenessTests
{
    static readonly UsnJournalCursor ArmedCursor = ScriptedWatchBrokerHarness.DefaultTip;

    // The journal has trimmed past the cursor every scan arms at.
    static readonly SyntheticJournalWindow TrimmedWindow = new(ArmedCursor.JournalId, 500, 900, 64, 4096);

    // The journal still holds the armed cursor.
    static readonly SyntheticJournalWindow RetainingWindow = new(ArmedCursor.JournalId, 50, 900, 64, 4096);

    /// <summary>
    ///     T's catch-up fails on every scan and the journal proves the armed cursor trimmed, so T's
    ///     scan operation scans three times, raises a <see cref="WatchFaultKind.CatchUpLost" /> fault for
    ///     each and stops with <see cref="JournalCatchUpLostException.RecoveryStopped" /> on the third,
    ///     keeping its third block unresumable. U rescans normally in the same run. Once the source is
    ///     fixed, a manual rescan of T succeeds, resets the count and accepts a start.
    /// </summary>
    [TestMethod]
    public async Task CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops()
    {
        var journal = new ScriptedCatchUp();
        using var window = JournalIsolation.OverrideJournalWindow(drive => drive == 'T' ? TrimmedWindow : null);
        await using var scenario = await CrossDriveScenario.OpenAsync(journal.ReadJournal);
        var index = scenario.Index;
        var token = scenario.Token;
        var hostClockAtStart = scenario.HostClock.GetUtcNow();
        var clientClockAtStart = scenario.ClientClock.GetUtcNow();
        var channelsBefore = scenario.ChannelsOpenedFor('T');
        journal.FailEveryRead();

        var rescanOfT = index.RescanAsync('T', token);
        var rescanOfU = index.RescanAsync('U', token);
        await rescanOfU.WaitAsync(HangGuard);
        var thrown = await WatchDeduplicationTestSupport.ThrowsAsync<JournalCatchUpLostException>(() => rescanOfT.WaitAsync(HangGuard));

        Assert.AreEqual(3, scenario.ChannelsOpenedFor('T') - channelsBefore, "T opens exactly three scan channels");
        Assert.AreEqual(4, scenario.ScansOf('T'), "the open's scan and three lost ones");
        var faults = scenario.FaultsOf('T');
        CollectionAssert.AreEqual(Enumerable.Repeat(WatchFaultKind.CatchUpLost, 3).ToArray(),
            faults.Select(fault => fault.Kind).ToArray(), "three CatchUpLost faults and no Recovery");
        var losses = faults.Select(fault => (JournalCatchUpLostException)fault.Exception).ToArray();
        var statusesAtLoss = scenario.CatchUpLossStatuses('T');
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, statusesAtLoss.Select(status => status.ConsecutiveLostCatchUps).ToArray());
        CollectionAssert.AreEqual(new[] { false, false, true }, losses.Select(loss => loss.RecoveryStopped).ToArray());
        Assert.AreSame(losses[2], thrown, "the rescan throws the loss that stopped it");

        var drive = scenario.DriveOf('T');
        Assert.AreEqual(3, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(1, index.FindByName("scan-T-4.txt", token).Count, "T keeps its third block, the fourth scan");
        Assert.AreEqual(0, index.FindByName("scan-T-3.txt", token).Count);
        var report = drive.CheckpointLoss;
        Assert.IsNotNull(report);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, report.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, report.Cause);
        Assert.AreEqual(ArmedCursor.NextUsn, report.CheckpointUsn);
        Assert.AreEqual(JournalSizeArithmetic.SizeThatWouldHaveRetained(ArmedCursor.NextUsn, TrimmedWindow.NextUsn,
            TrimmedWindow.AllocationDelta), report.SizeThatWouldHaveRetained);
        Assert.AreEqual(report, statusesAtLoss[^1].CheckpointLoss);
        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => index.StartWatchingAsync('T', token));

        // U completed normally in the same run.
        Assert.AreEqual(2, scenario.ScansOf('U'));
        Assert.AreEqual(1, index.FindByName("scan-U-2.txt", token).Count);
        Assert.AreEqual(0, scenario.FaultsOf('U').Count);
        Assert.AreEqual(0, scenario.DriveOf('U').ConsecutiveLostCatchUps);
        Assert.IsNull(scenario.DriveOf('U').CheckpointLoss);

        // The source is fixed: a manual rescan succeeds, the count resets and a start is accepted.
        journal.Fix();
        await index.RescanAsync('T', token).WaitAsync(HangGuard);
        Assert.AreEqual(0, scenario.DriveOf('T').ConsecutiveLostCatchUps);
        await index.StartWatchingAsync('T', token);
        Assert.AreEqual(ArmedCursor, (await scenario.Broker.Watch('T').RunAsync(1)).Since);
        Assert.AreEqual(hostClockAtStart, scenario.HostClock.GetUtcNow(), "no time passed on the host");
        Assert.AreEqual(clientClockAtStart, scenario.ClientClock.GetUtcNow(), "no time passed on the client");
    }

    /// <summary>
    ///     One lost catch-up: the rescan's second scan succeeds. The fault is raised once with a count
    ///     of one and the scan operation not stopped, the count returns to zero, the drive keeps the
    ///     <see cref="JournalCheckpointLossDetection.ScanCatchUp" /> report and its watch starts from
    ///     the second block's cursor.
    /// </summary>
    [TestMethod]
    public async Task CatchUpLostOnceOverBroker_SecondScanSucceeds()
    {
        var journal = new ScriptedCatchUp();
        using var window = JournalIsolation.OverrideJournalWindow(drive => drive == 'T' ? TrimmedWindow : null);
        await using var scenario = await CrossDriveScenario.OpenAsync(journal.ReadJournal);
        var index = scenario.Index;
        var token = scenario.Token;
        var channelsBefore = scenario.ChannelsOpenedFor('T');
        journal.FailNextReads(1);

        await index.RescanAsync('T', token).WaitAsync(HangGuard);

        Assert.AreEqual(2, scenario.ChannelsOpenedFor('T') - channelsBefore, "the lost scan and the one that held");
        var fault = scenario.FaultsOf('T').Single();
        Assert.AreEqual(WatchFaultKind.CatchUpLost, fault.Kind);
        var loss = (JournalCatchUpLostException)fault.Exception;
        Assert.AreEqual(1, scenario.CatchUpLossStatuses('T').Single().ConsecutiveLostCatchUps);
        Assert.IsFalse(loss.RecoveryStopped);
        var drive = scenario.DriveOf('T');
        Assert.AreEqual(0, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, drive.CheckpointLoss?.DetectedDuring,
            "the report the lost scan produced is kept");
        Assert.AreEqual(1, index.FindByName("scan-T-3.txt", token).Count, "T's block is the second scan's");
        Assert.AreEqual(0, index.FindByName("scan-T-2.txt", token).Count);
        await index.StartWatchingAsync('T', token);
        Assert.AreEqual(ArmedCursor, (await scenario.Broker.Watch('T').RunAsync(1)).Since,
            "the watch starts from the second block's cursor");
    }

    /// <summary>
    ///     The catch-up fails but the journal still holds the armed cursor, so nothing proves a loss:
    ///     the host writes <c>Error</c>, the rescan fails after exactly one scan channel with no
    ///     retry, the count is unchanged and the drive has no report and raised no fault.
    /// </summary>
    [TestMethod]
    public async Task CatchUpFailureNotProvenOverBroker_ScanFailsWithoutRetry()
    {
        var journal = new ScriptedCatchUp();
        using var window = JournalIsolation.OverrideJournalWindow(drive => drive == 'T' ? RetainingWindow : null);
        await using var scenario = await CrossDriveScenario.OpenAsync(journal.ReadJournal);
        var index = scenario.Index;
        var token = scenario.Token;
        var channelsBefore = scenario.ChannelsOpenedFor('T');
        journal.FailEveryRead();

        var failure = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(
            () => index.RescanAsync('T', token).WaitAsync(HangGuard));

        StringAssert.Contains(failure.Message, ScriptedCatchUp.FailureMessage);
        Assert.AreEqual(1, scenario.ChannelsOpenedFor('T') - channelsBefore, "one scan channel, no retry");
        Assert.AreEqual(2, scenario.ScansOf('T'));
        var drive = scenario.DriveOf('T');
        Assert.AreEqual(0, drive.ConsecutiveLostCatchUps);
        Assert.IsNull(drive.CheckpointLoss);
        Assert.AreEqual(0, scenario.Faults.Count, "no CatchUpLost fault");
        Assert.AreEqual(1, index.FindByName("scan-T-1.txt", token).Count, "the open's block stays");
    }

    /// <summary>The host's catch-up source: T's reads throw as scripted, U's hold at the cursor they are given.</summary>
    sealed class ScriptedCatchUp
    {
        public const string FailureMessage = "the journal read of T failed";

        int _failuresLeft;

        public void FailEveryRead() => Volatile.Write(ref _failuresLeft, int.MaxValue);

        public void FailNextReads(int count) => Volatile.Write(ref _failuresLeft, count);

        public void Fix() => Volatile.Write(ref _failuresLeft, 0);

        public (UsnJournalEntry[] Entries, UsnJournalCursor Cursor) ReadJournal(string driveLetter,
            UsnJournalCursor since, int maximumBufferReads)
        {
            if (driveLetter == "T" && Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw new IOException(FailureMessage);
            }

            return ([], since);
        }
    }
}
