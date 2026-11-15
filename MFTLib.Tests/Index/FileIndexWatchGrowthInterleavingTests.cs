using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchGrowthFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: a watch growth under a running pump, against a rescan that produces a replacement at the same
///     time, a stop and a disposal. The pump raises every change of a grown batch once, in order. A growth that meets
///     a producing rescan delivers what it applied, parks until the rescan publishes or gives up, and then either is
///     retired with the old lineage or grows and resumes.
/// </summary>
[TestClass]
public sealed class FileIndexWatchGrowthInterleavingTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    static SyntheticJournalCursor Cursor(long nextUsn) => new(JournalIdentifier, nextUsn);

    Task<WatchGrowthFixture> OpenAsync(Func<CancellationToken, Task>? beforeProduce = null) =>
        WatchGrowthFixture.OpenAsync(CacheDirectoryPath, TreeRoot, UniqueVolumeSerial, beforeProduce: beforeProduce);

    [TestMethod]
    public async Task APumpedBatchThatGrows_RaisesEveryChangeOnceInOrder()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();

        await watch.PublishBatchAsync([CreateRecord(10, "one.txt", 4100), CreateRecord(100, "far.txt", 4200),
            CreateRecord(11, "two.txt", 4300)], Cursor(5000));
        await watch.PublishBatchAsync([CreateRecord(12, "three.txt", 5100)], Cursor(6000));
        await fixture.WaitForChangeCountAsync(4);

        CollectionAssert.AreEqual(new[] { "one.txt", "far.txt", "two.txt", "three.txt" },
            fixture.Changes.Select(change => change.Entry.Name).ToArray());
        Assert.HasCount(1, fixture.GrowthLines);
        Assert.IsEmpty(fixture.Faults);
        Assert.AreEqual(6000L, fixture.Block.Header.UsnNextUsn);
    }

    /// <summary>A rescan queued behind a growth supersedes the grown block and renames the grown file aside.</summary>
    [TestMethod]
    public async Task AGrowthThenARescan_TheRescanSupersedesTheGrownBlock()
    {
        await using var fixture = await OpenAsync();
        await fixture.ApplyAsync(5000, Create(100, "far.txt", 4200));
        var grown = fixture.RootBlock;

        var result = await fixture.Index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.AreNotSame(grown.Mapped, fixture.RootBlock.Mapped);
        Assert.AreEqual(SmallCapacities, WatchGrowthPlan.CapacitiesOf(fixture.Block), "a rescan returns to its own plan");
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.growing-*"));
    }

    /// <summary>
    ///     A refusal while a rescan produces: the pump delivers what it applied before the refusal and parks; the rescan
    ///     publishes and retires the pump, so the parked records raise nothing, whether the refused USN is after the
    ///     rescan's cursor (the restarted watch replays it) or before it (the scan read its effect).
    /// </summary>
    [TestMethod]
    [DataRow(4100L, DisplayName = "the refused record is after the rescan's cursor")]
    [DataRow(3000L, DisplayName = "the refused record is before the rescan's cursor")]
    public async Task ARefusalDuringProduction_TheRescanPublishes_AndTheParkedRecordsRaiseNothing(long firstUsn)
    {
        var production = new TestGate();
        await using var fixture = await OpenAsync(async cancellationToken =>
        {
            production.MarkEntered();
            await production.WaitForReleaseAsync(cancellationToken);
        });
        var watch = await fixture.StartWatchingAsync();
        var before = fixture.RootBlock;
        var rescan = fixture.Index.RescanAsync(TestContext.CancellationToken);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = watch.QueueBatchAsync([CreateRecord(10, "applied.txt", firstUsn), CreateRecord(100, "parked.txt", firstUsn + 100),
            CreateRecord(11, "after.txt", firstUsn + 200)], Cursor(firstUsn + 1000));
        await fixture.WaitForChangeCountAsync(1);
        Assert.AreEqual(firstUsn + 100, before.Block.Header.UsnNextUsn, "the old block holds the refused record's cursor");
        production.Release();

        Assert.AreEqual(RootOperationOutcome.Succeeded, (await rescan.WaitAsync(HangGuard, TestContext.CancellationToken)).Outcome);
        await fixture.Watches.WaitForStartAsync(Volume, 2, TestContext.CancellationToken).WaitAsync(HangGuard);

        Assert.AreEqual("applied.txt", fixture.Changes.Single().Entry.Name);
        Assert.IsEmpty(fixture.GrowthLines, "the parked pump never grew the superseded block");
        Assert.AreEqual(StartUsn, fixture.Watches.TargetsFor(Volume)[^1].NextUsn, "the new watch replays from the scan's cursor");
        Assert.AreNotSame(before.Mapped, fixture.RootBlock.Mapped);
    }

    /// <summary>A refusal while a rescan produces, and the rescan fails: the parked pump grows and resumes.</summary>
    [TestMethod]
    public async Task ARefusalDuringProduction_TheRescanFails_AndThePumpGrowsAndResumes()
    {
        var production = new TestGate();
        await using var fixture = await OpenAsync(async cancellationToken =>
        {
            production.MarkEntered();
            await production.WaitForReleaseAsync(cancellationToken);
            throw new IOException("the rescan failed");
        });
        var watch = await fixture.StartWatchingAsync();
        var before = fixture.RootBlock;
        var rescan = fixture.Index.RescanAsync(TestContext.CancellationToken);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        _ = watch.QueueBatchAsync([CreateRecord(10, "applied.txt", 4100), CreateRecord(100, "parked.txt", 4200),
            CreateRecord(11, "after.txt", 4300)], Cursor(5000));
        await fixture.WaitForChangeCountAsync(1);
        production.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await fixture.WaitForChangeCountAsync(3);

        CollectionAssert.AreEqual(new[] { "applied.txt", "parked.txt", "after.txt" },
            fixture.Changes.Select(change => change.Entry.Name).ToArray());
        Assert.AreSame(before, fixture.RootBlock);
        Assert.HasCount(1, fixture.GrowthLines);
        Assert.AreEqual(5000L, fixture.Block.Header.UsnNextUsn);
    }

    /// <summary>
    ///     A stop during the copy cancels the growth: the replacement is deleted and the old block stays published at the
    ///     refused record's cursor, so a later watch replays that record.
    /// </summary>
    [TestMethod]
    public async Task AStopDuringTheCopy_DeletesTheReplacementAndKeepsTheOldBlockAtTheRefusedCursor()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        var before = fixture.Block;
        Task<RootOperationResult>? stop = null;
        var index = fixture.Index;
        index.Hooks.WatchGrowthStepReached = (_, step, cancellationToken) =>
        {
            if (step == FileIndex.WatchGrowthStep.ReplacementCreated)
            {
                stop = Task.Run(() => index.StopWatchingAsync(CancellationToken.None));
                Assert.IsTrue(cancellationToken.WaitHandle.WaitOne(HangGuard), "the stop reached the growth");
            }
        };

        _ = watch.QueueBatchAsync([CreateRecord(10, "applied.txt", 4100), CreateRecord(100, "far.txt", 4200)],
            Cursor(5000));
        await fixture.WaitForChangeCountAsync(1);
        Assert.IsNotNull(stop);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await stop.WaitAsync(HangGuard, TestContext.CancellationToken)).Outcome);

        Assert.AreSame(before, fixture.Block);
        Assert.AreEqual(4200L, fixture.Block.Header.UsnNextUsn);
        Assert.IsFalse(fixture.Block.Header.IsCapacityExhausted);
        Assert.IsEmpty(fixture.GrowthLines);
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.growing-*"));
        Assert.AreEqual("applied.txt", fixture.Changes.Single().Entry.Name);
    }

    /// <summary>A stop that arrives once the replacement holds the canonical name waits for the growth to publish.</summary>
    [TestMethod]
    public async Task AStopAfterTheRenames_WaitsForThePublicationAndTheBatch()
    {
        await using var fixture = await OpenAsync();
        var watch = await fixture.StartWatchingAsync();
        Task<RootOperationResult>? stop = null;
        var index = fixture.Index;
        index.Hooks.WatchGrowthStepReached = (_, step, cancellationToken) =>
        {
            if (step == FileIndex.WatchGrowthStep.RenamedIntoPlace)
            {
                stop = Task.Run(() => index.StopWatchingAsync(CancellationToken.None));
                Assert.IsTrue(cancellationToken.WaitHandle.WaitOne(HangGuard), "the stop reached the growth");
            }
        };

        _ = watch.QueueBatchAsync([CreateRecord(100, "far.txt", 4200), CreateRecord(11, "after.txt", 4300)],
            Cursor(5000));
        await fixture.WaitForChangeCountAsync(2);
        await stop!.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(1, fixture.GrowthLines);
        Assert.AreEqual(5000L, fixture.Block.Header.UsnNextUsn);
        Assert.AreEqual(BlockLayout.PlanCapacities(101, SmallCapacities), WatchGrowthPlan.CapacitiesOf(fixture.Block));
    }

    /// <summary>
    ///     A disposal at each step of a growth: before the renames it cancels the growth and the old block stays in the
    ///     slot at the refused record's cursor; from the renames on the slot holds the replacement, published or not. In
    ///     every case the next open adopts the slot's block at the refused cursor or the batch cursor, and no sibling
    ///     survives it.
    /// </summary>
    [TestMethod]
    [DataRow(nameof(FileIndex.WatchGrowthStep.ReplacementCreated), false, DisplayName = "after the replacement is created")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.Copied), false, DisplayName = "after the copy")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.RenamedAside), true, DisplayName = "between the renames")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.RenamedIntoPlace), true, DisplayName = "after the renames")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.Published), true, DisplayName = "after publication")]
    public async Task ADisposalAtEachStep_LeavesAnAdoptableSlot(string atStep, bool grown)
    {
        var at = Enum.Parse<FileIndex.WatchGrowthStep>(atStep);
        var fixture = await OpenAsync();
        Task? disposal = null;
        fixture.Index.Hooks.WatchGrowthStepReached = (_, step, cancellationToken) =>
        {
            if (step == at)
            {
                disposal = fixture.Index.DisposeAsync().AsTask();
                Assert.IsTrue(cancellationToken.WaitHandle.WaitOne(HangGuard), "the disposal reached the growth");
            }
        };

        try
        {
            await fixture.ApplyAsync(5000, Create(100, "far.txt", 4200));
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException)
        {
            // The disposal ended the batch.
        }

        await disposal!.WaitAsync(HangGuard, TestContext.CancellationToken);
        await using var reopened = await OpenAsync();

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Index.Status.Block!.Origin);
        var expected = grown ? BlockLayout.PlanCapacities(101, SmallCapacities) : SmallCapacities;
        Assert.AreEqual(expected, WatchGrowthPlan.CapacitiesOf(reopened.Block));
        Assert.Contains(reopened.Block.Header.UsnNextUsn, new[] { 4200L, 5000L });
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.growing-*"));
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.retired-*"));
    }
}
