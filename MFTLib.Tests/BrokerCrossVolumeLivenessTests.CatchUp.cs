using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Lost catch-up over the broker (spec 2.6.6, level L1): the host's catch-up source throws, the
// journal window says whether the armed cursor is still retained, and the index's scan operation
// counts, retries and stops. Both clocks stay where they started: nothing here depends on time.
public sealed partial class BrokerCrossVolumeLivenessTests
{
    static readonly UsnJournalCursor ArmedCursor = ScriptedWatchBrokerHarness.DefaultTip;

    // The journal has trimmed past the cursor every scan arms at.
    static readonly SyntheticJournalWindow TrimmedWindow = new(ArmedCursor.JournalIdentifier, 500, 900, 64, 4096);

    // The journal still holds the armed cursor.
    static readonly SyntheticJournalWindow RetainingWindow = new(ArmedCursor.JournalIdentifier, 50, 900, 64, 4096);

    /// <summary>
    ///     T's catch-up fails on every scan and the journal proves the armed cursor trimmed, so T's
    ///     scan operation scans three times, raises a <see cref="WatchFaultKind.CatchUpLost" /> fault for
    ///     each and stops with <see cref="JournalCatchUpLostException.IsRecoveryStopped" /> on the third,
    ///     keeping its third block unresumable. U rescans normally in the same run. Once the source is
    ///     fixed, a manual rescan of T succeeds, resets the count and accepts a start.
    /// </summary>
    [TestMethod]
    public async Task CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops()
    {
        var journal = new ScriptedCatchUp();
        using var window = JournalIsolation.OverrideJournalWindow(volume => volume == 'T' ? TrimmedWindow : null);
        await using var scenario = await CrossRootScenario.OpenAsync(journal.ReadJournal);
        var indexT = scenario.IndexFor('T');
        var indexU = scenario.IndexFor('U');
        var token = scenario.Token;
        var hostClockAtStart = scenario.HostClock.GetUtcNow();
        var clientClockAtStart = scenario.ClientClock.GetUtcNow();
        var channelsBefore = scenario.ChannelsOpenedFor('T');
        journal.FailEveryRead();

        var rescanOfT = indexT.RescanAsync(token);
        var rescanOfU = indexU.RescanAsync(token);
        await rescanOfU.WaitAsync(HangGuard, TestContext.CancellationToken);
        var failure = await RootResultAssertions.FailureAsync<JournalCatchUpLostException>(() => rescanOfT.WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(3, scenario.ChannelsOpenedFor('T') - channelsBefore, "T opens exactly three scan channels");
        Assert.AreEqual(4, scenario.ScansOf('T'), "the open's scan and three lost ones");
        var faults = scenario.FaultsOf('T');
        Assert.AreSequenceEqual([.. Enumerable.Repeat(WatchFaultKind.CatchUpLost, 3)], [.. faults.Select(fault => fault.Kind)], "three CatchUpLost faults and no Recovery");
        var losses = faults.Select(fault => (JournalCatchUpLostException)fault.Exception).ToArray();
        var statusesAtLoss = scenario.CatchUpLossStatuses('T');
        Assert.AreSequenceEqual([1, 2, 3], [.. statusesAtLoss.Select(status => status.Watch.ConsecutiveLostCatchUps)]);
        Assert.AreSequenceEqual([false, false, true], [.. losses.Select(loss => loss.IsRecoveryStopped)]);
        Assert.AreSame(losses[2], failure, "the rescan reports the loss that stopped it");

        var root = scenario.RootOf('T');
        Assert.AreEqual(3, root.Watch.ConsecutiveLostCatchUps);
        Assert.HasCount(1, indexT.Search(new SearchQuery("scan-T-4.txt", NameMatchMode.Exact), token), "T keeps its third block, the fourth scan");
        Assert.IsEmpty(indexT.Search(new SearchQuery("scan-T-3.txt", NameMatchMode.Exact), token));
        var report = root.Watch.CheckpointLoss;
        Assert.IsNotNull(report);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, report.DetectedDuring);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, report.Cause);
        Assert.AreEqual(ArmedCursor.NextUsn, report.CheckpointUsn);
        Assert.AreEqual(JournalSizeArithmetic.SizeThatWouldHaveRetained(ArmedCursor.NextUsn, TrimmedWindow.NextUsn,
            TrimmedWindow.AllocationDelta), report.SizeThatWouldHaveRetained);
        Assert.AreEqual(report, statusesAtLoss[^1].Watch.CheckpointLoss);
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => indexT.StartWatchingAsync(token));

        // U completed normally in the same run.
        Assert.AreEqual(2, scenario.ScansOf('U'));
        Assert.HasCount(1, indexU.Search(new SearchQuery("scan-U-2.txt", NameMatchMode.Exact), token));
        Assert.IsEmpty(scenario.FaultsOf('U'));
        Assert.AreEqual(0, scenario.RootOf('U').Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(scenario.RootOf('U').Watch.CheckpointLoss);

        // The source is fixed: a manual rescan succeeds, the count resets and a start is accepted.
        journal.Fix();
        await indexT.RescanAsync(token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(0, scenario.RootOf('T').Watch.ConsecutiveLostCatchUps);
        await indexT.StartWatchingAsync(token);
        Assert.AreEqual(ArmedCursor, (await scenario.Broker.Watch('T').RunAsync(1)).Since);
        Assert.AreEqual(hostClockAtStart, scenario.HostClock.GetUtcNow(), "no time passed on the host");
        Assert.AreEqual(clientClockAtStart, scenario.ClientClock.GetUtcNow(), "no time passed on the client");
    }

    /// <summary>
    ///     One lost catch-up: the rescan's second scan succeeds. The fault is raised once with a count
    ///     of one and the scan operation not stopped, the count returns to zero, the volume keeps the
    ///     <see cref="JournalCheckpointLossDetection.ScanCatchUp" /> report and its watch starts from
    ///     the second block's cursor.
    /// </summary>
    [TestMethod]
    public async Task CatchUpLostOnceOverBroker_SecondScanSucceeds()
    {
        var journal = new ScriptedCatchUp();
        using var window = JournalIsolation.OverrideJournalWindow(volume => volume == 'T' ? TrimmedWindow : null);
        await using var scenario = await CrossRootScenario.OpenAsync(journal.ReadJournal);
        var indexT = scenario.IndexFor('T');
        var token = scenario.Token;
        var channelsBefore = scenario.ChannelsOpenedFor('T');
        journal.FailNextReads(1);

        await indexT.RescanAsync(token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(2, scenario.ChannelsOpenedFor('T') - channelsBefore, "the lost scan and the one that held");
        var fault = scenario.FaultsOf('T').Single();
        Assert.AreEqual(WatchFaultKind.CatchUpLost, fault.Kind);
        var loss = (JournalCatchUpLostException)fault.Exception;
        Assert.AreEqual(1, scenario.CatchUpLossStatuses('T').Single().Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(loss.IsRecoveryStopped);
        var root = scenario.RootOf('T');
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.AreEqual(JournalCheckpointLossDetection.ScanCatchUp, root.Watch.CheckpointLoss?.DetectedDuring,
            "the report the lost scan produced is kept");
        Assert.HasCount(1, indexT.Search(new SearchQuery("scan-T-3.txt", NameMatchMode.Exact), token), "T's block is the second scan's");
        Assert.IsEmpty(indexT.Search(new SearchQuery("scan-T-2.txt", NameMatchMode.Exact), token));
        await indexT.StartWatchingAsync(token);
        Assert.AreEqual(ArmedCursor, (await scenario.Broker.Watch('T').RunAsync(1)).Since,
            "the watch starts from the second block's cursor");
    }

    /// <summary>
    ///     The catch-up fails but the journal still holds the armed cursor, so nothing proves a loss:
    ///     the host writes <c>Error</c>, the rescan fails after exactly one scan channel with no
    ///     retry, the count is unchanged and the volume has no report and raised no fault.
    /// </summary>
    [TestMethod]
    public async Task CatchUpFailureNotProvenOverBroker_ScanFailsWithoutRetry()
    {
        var journal = new ScriptedCatchUp();
        using var window = JournalIsolation.OverrideJournalWindow(volume => volume == 'T' ? RetainingWindow : null);
        await using var scenario = await CrossRootScenario.OpenAsync(journal.ReadJournal);
        var indexT = scenario.IndexFor('T');
        var token = scenario.Token;
        var channelsBefore = scenario.ChannelsOpenedFor('T');
        journal.FailEveryRead();

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => indexT.RescanAsync(token).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains(ScriptedCatchUp.FailureMessage, failure.Message);
        Assert.AreEqual(1, scenario.ChannelsOpenedFor('T') - channelsBefore, "one scan channel, no retry");
        Assert.AreEqual(2, scenario.ScansOf('T'));
        var root = scenario.RootOf('T');
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsNull(root.Watch.CheckpointLoss);
        Assert.IsEmpty(scenario.Faults, "no CatchUpLost fault");
        Assert.HasCount(1, indexT.Search(new SearchQuery("scan-T-1.txt", NameMatchMode.Exact), token), "the open's block stays");
    }

    /// <summary>The host's catch-up source: T's reads throw as scripted, U's hold at the cursor they are given.</summary>
    sealed class ScriptedCatchUp
    {
        public const string FailureMessage = "the journal read of T failed";

        int _failuresLeft;

        public void FailEveryRead() => Volatile.Write(ref _failuresLeft, int.MaxValue);

        public void FailNextReads(int count) => Volatile.Write(ref _failuresLeft, count);

        public void Fix() => Volatile.Write(ref _failuresLeft, 0);

        public (UsnJournalEntry[] Entries, UsnJournalCursor Cursor) ReadJournal(VolumeKey volume,
            UsnJournalCursor since, int maximumBufferReads)
        {
            if (volume == 'T' && Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw new IOException(FailureMessage);
            }

            return ([], since);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
