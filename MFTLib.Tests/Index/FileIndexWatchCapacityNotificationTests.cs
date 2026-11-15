using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchGrowthFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953, cannot grow: a journal change the watch skips because no growth can store it raises
///     <see cref="WatchFaultKind.CapacityExhausted" /> once per block lineage, after that batch's changes, with the cause
///     as the exception. It is not a watch failure: no outstanding fault, stop succeeds, no recovery, no state change.
/// </summary>
[TestClass]
public sealed class FileIndexWatchCapacityNotificationTests : IndexTreeTestBase
{
    /// <summary>A record past the largest slot region a block can plan, but within 32 bits: its growth plan clamps.</summary>
    const ulong UnplannableRecord = 2_000_000_000;

    /// <summary>A record above 32 bits, which no block can store.</summary>
    const ulong OutOfRangeRecord = (ulong)uint.MaxValue + 1;

    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    static SyntheticJournalCursor Cursor(long nextUsn) => new(JournalIdentifier, nextUsn);

    Task<WatchGrowthFixture> OpenAsync() =>
        WatchGrowthFixture.OpenAsync(CacheDirectoryPath, TreeRoot, UniqueVolumeSerial);

    /// <summary>The order changes and faults reach subscribers in, by name and by kind.</summary>
    static ConcurrentQueue<string> RecordEvents(FileIndex index)
    {
        var events = new ConcurrentQueue<string>();
        index.Changed += change => events.Enqueue("changed " + change.Entry.Name);
        index.WatchFaulted += fault => events.Enqueue("faulted " + fault.Kind);
        return events;
    }

    async Task AssertTheWatchIsHealthyAsync(WatchGrowthFixture fixture, Func<int> stateChanges)
    {
        Assert.AreEqual(WatchState.CatchingUp, fixture.Index.Status.Watch.State, "the watch never caught up, and never faulted");
        Assert.IsNull(fixture.Index.Status.Watch.FailureMessage);
        Assert.AreEqual(0, stateChanges(), "the notification changes no watch state");
        var stop = await fixture.Index.StopWatchingAsync(TestContext.CancellationToken);
        Assert.AreEqual(RootOperationOutcome.Succeeded, stop.Outcome, "the notification leaves no outstanding fault");
    }

    /// <summary>Counts the growth attempts a hook sees, across the pump's thread and the test's.</summary>
    sealed class GrowthAttempts
    {
        int _total;

        public int Total => Volatile.Read(ref _total);

        /// <summary>Counts one attempt and returns how many there have been.</summary>
        public int Count() => Interlocked.Increment(ref _total);
    }

    static Func<int> CountStateChanges(FileIndex index)
    {
        var count = 0;
        index.WatchStateChanged += _ => Interlocked.Increment(ref count);
        return () => Volatile.Read(ref count);
    }

    [TestMethod]
    public async Task AnOutOfRangeRecord_NotifiesOnceAfterTheBatchsChanges_WithTheCause()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var stateChanges = CountStateChanges(fixture.Index);
        var events = RecordEvents(fixture.Index);

        await watch.PublishBatchAsync([CreateRecord(10, "before.txt", 4100), CreateRecord(OutOfRangeRecord, "huge.txt", 4200),
            CreateRecord(11, "after.txt", 4300)], Cursor(5000));

        CollectionAssert.AreEqual(new[] { "changed before.txt", "changed after.txt", "faulted CapacityExhausted" },
            events.ToArray());
        var fault = fixture.Faults.Single();
        Assert.AreEqual(WatchFaultKind.CapacityExhausted, fault.Kind);
        Assert.AreEqual(Volume, fault.Volume);
        Assert.IsInstanceOfType<InvalidOperationException>(fault.Exception);
        StringAssert.Contains(fault.Exception.Message, "above 32 bits");
        Assert.IsFalse(fault.IsRecovering);
        Assert.IsTrue(fixture.Index.Status.Block!.IsCapacityExhausted);
        Assert.AreEqual(5000L, fixture.Block.Header.UsnNextUsn);
        await AssertTheWatchIsHealthyAsync(fixture, stateChanges);
    }

    [TestMethod]
    public async Task AGrowthPlanAtItsClamp_NotifiesWithTheClampAsTheCause()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var stateChanges = CountStateChanges(fixture.Index);

        await watch.PublishBatchAsync([CreateRecord(UnplannableRecord, "far.txt", 4100), CreateRecord(11, "next.txt", 4200)],
            Cursor(5000));

        var fault = fixture.Faults.Single();
        Assert.AreEqual(WatchFaultKind.CapacityExhausted, fault.Kind);
        Assert.IsInstanceOfType<InvalidOperationException>(fault.Exception);
        StringAssert.Contains(fault.Exception.Message, "largest slot region");
        Assert.AreEqual("next.txt", fixture.Changes.Single().Entry.Name);
        Assert.IsEmpty(fixture.GrowthLines);
        await AssertTheWatchIsHealthyAsync(fixture, stateChanges);
    }

    [TestMethod]
    public async Task ARecordRefusedAgainRightAfterItsGrowth_NotifiesOnce()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var stateChanges = CountStateChanges(fixture.Index);

        await watch.PublishBatchAsync([CreateRecord(20, new string('x', 40000), 4100), CreateRecord(21, "next.txt", 4200)],
            Cursor(5000));

        var fault = fixture.Faults.Single();
        Assert.AreEqual(WatchFaultKind.CapacityExhausted, fault.Kind);
        StringAssert.Contains(fault.Exception.Message, "again right after a growth");
        Assert.HasCount(1, fixture.GrowthLines);
        await AssertTheWatchIsHealthyAsync(fixture, stateChanges);
    }

    /// <summary>
    ///     A growth that fails with an I/O error notifies with that error. The rest of the batch does not grow again; the
    ///     next refusal in a later batch does, and its growth succeeds, without a second notification.
    /// </summary>
    [TestMethod]
    public async Task AGrowthIOFailure_NotifiesWithTheErrorAndIsRetriedInTheNextBatchOnly()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var stateChanges = CountStateChanges(fixture.Index);
        var attempts = new GrowthAttempts();
        var failure = new IOException("the replacement could not be written");
        fixture.Index.Hooks.WatchGrowthStepReached = (_, step, _) =>
        {
            if (step == FileIndex.WatchGrowthStep.ReplacementCreated && attempts.Count() == 1)
            {
                throw failure;
            }
        };

        await watch.PublishBatchAsync([CreateRecord(100, "first.txt", 4100), CreateRecord(101, "second.txt", 4200)],
            Cursor(5000));

        Assert.AreEqual(1, attempts.Total, "a batch makes at most one growth attempt");
        var fault = fixture.Faults.Single();
        Assert.AreEqual(WatchFaultKind.CapacityExhausted, fault.Kind);
        Assert.AreSame(failure, fault.Exception);
        Assert.IsEmpty(fixture.Changes);

        await watch.PublishBatchAsync([CreateRecord(102, "third.txt", 5100)], Cursor(6000));

        Assert.AreEqual(2, attempts.Total, "the next refusal in a later batch grows again");
        Assert.HasCount(1, fixture.GrowthLines);
        Assert.AreEqual("third.txt", fixture.Changes.Single().Entry.Name);
        Assert.HasCount(1, fixture.Faults, "the lineage already notified");
        await AssertTheWatchIsHealthyAsync(fixture, stateChanges);
    }

    [TestMethod]
    public async Task ASecondUnstorableRecordOnTheSameLineage_DoesNotNotifyAgain()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();

        await watch.PublishBatchAsync([CreateRecord(OutOfRangeRecord, "one.txt", 4100), CreateRecord(OutOfRangeRecord + 1, "two.txt", 4200)],
            Cursor(5000));
        await watch.PublishBatchAsync([CreateRecord(OutOfRangeRecord + 2, "three.txt", 5100)], Cursor(6000));
        await watch.PublishBatchAsync([CreateRecord(12, "stored.txt", 6100)], Cursor(7000));

        Assert.HasCount(1, fixture.Faults);
        Assert.AreEqual("stored.txt", fixture.Changes.Single().Entry.Name);
        Assert.AreEqual(3, fixture.DiagnosticsLines.Count(line => line.StartsWith("Skipped journal record", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     A name region at its clamp is remembered by the lineage: a later refusal by that region, in any batch, is skipped
    ///     with the remembered cause and no growth is attempted. A rescan starts a new lineage, which grows again.
    /// </summary>
    [TestMethod]
    public async Task ARememberedNameRegionClamp_IsNotRetriedWithinTheLineage_AndARescanStartsClean()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var clamp = new InvalidOperationException("The name pool region is at its clamp of 2147479552.");
        fixture.RootBlock.Mapped.Capacity.RememberClamp(WriteRefusal.NamePool, clamp);
        var longNames = Enumerable.Range(0, 5)
            .Select(offset => CreateRecord(20 + (ulong)offset, new string((char)('a' + offset), 1000), 4100 + offset)).ToArray();

        await watch.PublishBatchAsync(longNames, Cursor(5000));

        Assert.IsEmpty(fixture.GrowthLines);
        Assert.AreSame(clamp, fixture.Faults.Single().Exception);
        Assert.HasCount(4, fixture.Changes, "the pool held four of the names");

        var rescan = await fixture.Index.RescanAsync(TestContext.CancellationToken);
        Assert.AreEqual(RootOperationOutcome.Succeeded, rescan.Outcome);
        var restarted = await fixture.Watches.WaitForStartAsync(Volume, 2, TestContext.CancellationToken).WaitAsync(HangGuard);
        await restarted.PublishBatchAsync(longNames, Cursor(5000));

        Assert.HasCount(1, fixture.GrowthLines, "the new lineage remembers no clamp");
        Assert.HasCount(1, fixture.Faults);
    }

    [TestMethod]
    public async Task ARescan_StartsANewLineageThatCanNotifyAgain()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        await watch.PublishBatchAsync([CreateRecord(OutOfRangeRecord, "one.txt", 4100)], Cursor(5000));
        Assert.HasCount(1, fixture.Faults);

        var rescan = await fixture.Index.RescanAsync(TestContext.CancellationToken);
        Assert.AreEqual(RootOperationOutcome.Succeeded, rescan.Outcome);
        Assert.IsFalse(fixture.Index.Status.Block!.IsCapacityExhausted, "the rescanned block stored every change");
        var restarted = await fixture.Watches.WaitForStartAsync(Volume, 2, TestContext.CancellationToken).WaitAsync(HangGuard);
        await restarted.PublishBatchAsync([CreateRecord(OutOfRangeRecord, "one.txt", 4100)], Cursor(5000));

        Assert.HasCount(2, fixture.Faults);
        Assert.IsTrue(fixture.Faults.All(fault => fault.Kind == WatchFaultKind.CapacityExhausted));
    }

    /// <summary>A direct journal apply has no pump and no requesting view, so it skips and flags without a notification.</summary>
    [TestMethod]
    public async Task ADirectApply_FlagsWithoutNotifying_AndLeavesTheLineagesNotificationForItsWatch()
    {
        await using var fixture = await OpenAsync();
        await fixture.ApplyAsync(5000, Create(OutOfRangeRecord, "direct.txt", 4100));
        Assert.IsEmpty(fixture.Faults);

        var watch = await fixture.StartWatchingAsync();
        await watch.PublishBatchAsync([CreateRecord(OutOfRangeRecord, "pumped.txt", 5100)], Cursor(6000));

        Assert.AreEqual(WatchFaultKind.CapacityExhausted, fixture.Faults.Single().Kind);
    }

    /// <summary>
    ///     A rescan that publishes while the old pump is still delivering the batch's changes supersedes the lineage the
    ///     record was skipped on, so the old pump raises nothing: the healthy replacement must not read as incomplete.
    /// </summary>
    [TestMethod]
    public async Task ARescanPublishedDuringTheBatchsChangedDelivery_SuppressesTheStaleNotification()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var skippedOn = fixture.RootBlock.Mapped;
        var delivering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Index.Changed += change =>
        {
            if (change.Entry.Name == "applied.txt")
            {
                delivering.TrySetResult();
                // The handler runs on the pump, which must stay inside Changed delivery until the rescan published.
                release.Task.Wait(HangGuard);
            }
        };

        var batch = watch.QueueBatchAsync([CreateRecord(10, "applied.txt", 4100), CreateRecord(OutOfRangeRecord, "huge.txt", 4200)],
            Cursor(5000));
        await delivering.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        var index = fixture.Index;
        var rescan = index.RescanAsync(TestContext.CancellationToken);
        Assert.IsTrue(SpinWait.SpinUntil(() => !ReferenceEquals(index.CurrentBlock?.Mapped, skippedOn), HangGuard),
            "the rescan published its replacement while the old pump was still delivering");
        release.TrySetResult();

        Assert.AreEqual(RootOperationOutcome.Succeeded, (await rescan.WaitAsync(HangGuard, TestContext.CancellationToken)).Outcome);
        await batch.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsFalse(fixture.Index.Status.Block!.IsCapacityExhausted);
        Assert.IsFalse(fixture.Faults.Any(fault => fault.Kind == WatchFaultKind.CapacityExhausted),
            "the superseded lineage's skip is not reported against the replacement");
    }

    /// <summary>A growth keeps the lineage, so a record skipped after a growth in the same batch still notifies.</summary>
    [TestMethod]
    public async Task ASkipAfterASameLineageGrowth_StillNotifies()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var lineage = fixture.RootBlock.Mapped;

        await watch.PublishBatchAsync([CreateRecord(100, "far.txt", 4100), CreateRecord(OutOfRangeRecord, "huge.txt", 4200)],
            Cursor(5000));

        Assert.HasCount(1, fixture.GrowthLines);
        Assert.AreSame(lineage, fixture.RootBlock.Mapped);
        Assert.AreEqual(WatchFaultKind.CapacityExhausted, fixture.Faults.Single().Kind);
        Assert.AreEqual("far.txt", fixture.Changes.Single().Entry.Name);
    }
}
