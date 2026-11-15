using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchGrowthFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: a journal record the block refuses grows the block and is applied to the replacement, with the
///     same root block, so no change is lost or applied twice. Each growable region grows as planned; a record no growth
///     can store is flagged and skipped as before.
/// </summary>
[TestClass]
public sealed class FileIndexWatchGrowthTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    Task<WatchGrowthFixture> OpenAsync(BlockCapacities? capacities = null, CacheMode cacheMode = CacheMode.Persistent) =>
        WatchGrowthFixture.OpenAsync(CacheDirectoryPath, TreeRoot, UniqueVolumeSerial, capacities,
            cacheMode: cacheMode);

    [TestMethod]
    public async Task SlotRefusal_GrowsAndAppliesEveryRecordOnceInOrder()
    {
        await using var fixture = await OpenAsync();

        var changes = await fixture.ApplyAsync(5000,
            Create(10, "before.txt", 4100), Create(100, "far.txt", 4200), Create(11, "after.txt", 4300));

        CollectionAssert.AreEqual(new[] { "before.txt", "far.txt", "after.txt" },
            changes.Select(change => change.Entry.Name).ToArray());
        var planned = BlockLayout.PlanCapacities(101, SmallCapacities);
        Assert.AreEqual(planned, WatchGrowthPlan.CapacitiesOf(fixture.Block));
        Assert.AreEqual(5000L, fixture.Block.Header.UsnNextUsn);
        Assert.IsFalse(fixture.Block.Header.IsCapacityExhausted);
        Assert.AreEqual(100u, fixture.Index.Find(Path.Combine(TreeRoot, "far.txt"), TestContext.CancellationToken)?.RowIndex);
        Assert.HasCount(1, fixture.GrowthLines);
        StringAssert.Contains(fixture.GrowthLines[0], "USN 4200 (record 100) was refused by the Slots region");
    }

    [TestMethod]
    public async Task NameEntryRefusal_DoublesOnlyTheEntryRegion()
    {
        await using var fixture = await OpenAsync(SmallCapacities with { NameEntryCapacity = 8 });
        var entries = Enumerable.Range(0, 8).Select(offset => Create(20 + (ulong)offset, $"n{offset}.txt", 4100 + offset))
            .ToArray();

        var changes = await fixture.ApplyAsync(5000, entries);

        Assert.HasCount(8, changes);
        Assert.AreEqual(SmallCapacities with { NameEntryCapacity = 16 }, WatchGrowthPlan.CapacitiesOf(fixture.Block));
        Assert.HasCount(1, fixture.GrowthLines);
        StringAssert.Contains(fixture.GrowthLines[0], "NameEntries region");
    }

    [TestMethod]
    public async Task NamePoolRefusal_DoublesOnlyThePool()
    {
        await using var fixture = await OpenAsync();
        var entries = Enumerable.Range(0, 5)
            .Select(offset => Create(20 + (ulong)offset, new string((char)('a' + offset), 1000), 4100 + offset)).ToArray();

        var changes = await fixture.ApplyAsync(5000, entries);

        Assert.HasCount(5, changes);
        Assert.AreEqual(SmallCapacities with { NamePoolCapacity = 8192 }, WatchGrowthPlan.CapacitiesOf(fixture.Block));
        StringAssert.Contains(fixture.GrowthLines.Single(), "NamePool region");
    }

    [TestMethod]
    public async Task DeltaRefusal_DoublesTheDeltaAndRebuildsThePostings()
    {
        await using var fixture = await OpenAsync();
        var entries = Enumerable.Range(0, 20).Select(offset => Create(20 + (ulong)offset, $"d{offset}.txt", 4100 + offset))
            .ToArray();

        var changes = await fixture.ApplyAsync(5000, entries);

        Assert.HasCount(20, changes);
        Assert.AreEqual(SmallCapacities with { DeltaCapacity = 32 }, WatchGrowthPlan.CapacitiesOf(fixture.Block));
        Assert.AreEqual(4u, fixture.Block.DeltaEntryCount, "the rebuilt base held the first 16; the last 4 are in the delta");
        Assert.IsNull(PostingIntegrity.Check(fixture.Block));
        Assert.HasCount(1, fixture.Index.Search(new SearchQuery("d19.txt", NameMatchMode.Exact),
            TestContext.CancellationToken));
    }

    /// <summary>
    ///     The copy carries the header and the lineage's cycles; it changes no row, so the generation, the counts and every
    ///     status field read the same on the replacement, and the growth raises no event.
    /// </summary>
    [TestMethod]
    public async Task Growth_CarriesTheHeaderAndCyclesAndRaisesNoEvent()
    {
        await using var fixture = await OpenAsync();
        await fixture.ApplyAsync(4200, Entry(12, RootRow, "open.txt", UsnReason.FileCreate, 4150));
        var statusBefore = fixture.Index.Status;
        var cyclesBefore = fixture.RootBlock.ReportedCycles;
        var rootBlockBefore = fixture.RootBlock;
        var stateChanges = 0;
        fixture.Index.WatchStateChanged += _ => Interlocked.Increment(ref stateChanges);
        BlockHeader retiredHeader = default, replacementHeader = default;
        RootStatus? statusAtPublication = null;
        var retired = fixture.Block;
        var index = fixture.Index;
        index.Hooks.WatchGrowthStepReached = (_, step, _) =>
        {
            if (step == FileIndex.WatchGrowthStep.Published)
            {
                retiredHeader = retired.Header;
                replacementHeader = rootBlockBefore.Block.Header;
                statusAtPublication = index.Status;
            }
        };

        // The refused record comes first, so nothing is applied before the growth.
        var changes = await fixture.ApplyAsync(5000, Create(100, "far.txt", 4300),
            Entry(12, RootRow, "open.txt", UsnReason.FileCreate | UsnReason.Close, 4400));

        Assert.AreSame(rootBlockBefore, fixture.RootBlock);
        Assert.AreSame(cyclesBefore, fixture.RootBlock.ReportedCycles);
        Assert.AreEqual("far.txt", changes.Single().Entry.Name, "the close of a cycle reported before the growth is an echo");
        Assert.AreEqual(retiredHeader.Generation, replacementHeader.Generation);
        Assert.AreEqual(4300L, replacementHeader.UsnNextUsn);
        Assert.AreEqual(retiredHeader.UsnJournalIdentifier, replacementHeader.UsnJournalIdentifier);
        Assert.AreEqual(retiredHeader.LiveRowCount, replacementHeader.LiveRowCount);
        Assert.AreEqual(retiredHeader.RowCount, replacementHeader.RowCount);
        Assert.AreEqual(retiredHeader.ScanTimestampTicks, replacementHeader.ScanTimestampTicks);
        Assert.AreEqual(retiredHeader.ScanShape, replacementHeader.ScanShape);
        Assert.AreEqual(retiredHeader.CacheTag, replacementHeader.CacheTag);
        Assert.AreEqual(retiredHeader.RootRow, replacementHeader.RootRow);
        Assert.IsTrue((replacementHeader.Flags & BlockFlags.Complete) != 0);
        Assert.AreEqual(statusBefore.State, statusAtPublication!.State);
        Assert.AreEqual(statusBefore.Block, statusAtPublication.Block);
        Assert.AreEqual(0, stateChanges);
    }

    [TestMethod]
    public async Task HandlesHeldAcrossAGrowth_StayEqualAndLive()
    {
        await using var fixture = await OpenAsync();
        var held = fixture.Index.Find(Path.Combine(TreeRoot, "docs", "b.txt"), TestContext.CancellationToken)!.Value;
        var before = fixture.Block;

        await fixture.ApplyAsync(5000, Create(100, "far.txt", 4200), Rename(NestedFileRow, "c.txt", 4300, DirectoryRow));

        Assert.AreNotSame(before, fixture.Block);
        Assert.AreEqual(FileEntry.Create(fixture.RootBlock, NestedFileRow), held);
        Assert.IsFalse(held.IsDisposed);
        Assert.AreEqual("c.txt", held.Name);
        Assert.AreEqual("docs", held.Parent?.Name);
        Assert.AreEqual(Path.Combine(TreeRoot, "docs", "c.txt"), held.Path);
    }

    [TestMethod]
    public async Task ARowEnumerationInFlight_FinishesOnTheMappingItPinned()
    {
        await using var fixture = await OpenAsync();

        var names = EnumerateGrowingOnTheFirstRow(fixture, TestContext.CancellationToken);

        CollectionAssert.Contains(names, "b.txt");
        CollectionAssert.DoesNotContain(names, "far.txt");
        Assert.HasCount(1, fixture.GrowthLines);
        Assert.HasCount(1, fixture.Index.Search(new SearchQuery("far.txt", NameMatchMode.Exact),
            TestContext.CancellationToken));
    }

    /// <summary>Enumerates every row, growing the block once the first row is read; the enumerator is a ref struct, so this blocks.</summary>
    static List<string> EnumerateGrowingOnTheFirstRow(WatchGrowthFixture fixture, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var row in fixture.Index.EnumerateRows(new SearchQuery(null), cancellationToken))
        {
            if (names.Count == 0)
            {
                fixture.ApplyAsync(5000, Create(100, "far.txt", 4200)).WaitAsync(HangGuard, cancellationToken)
                    .GetAwaiter().GetResult();
            }

            names.Add(row.Name.ToString());
        }

        return names;
    }

    /// <summary>A name no growth can store is refused by the same region right after its growth, so it is skipped, once.</summary>
    [TestMethod]
    public async Task ARecordRefusedAgainRightAfterItsGrowth_IsSkippedAndTheBatchContinues()
    {
        await using var fixture = await OpenAsync();

        var changes = await fixture.ApplyAsync(5000, Create(20, new string('x', 40000), 4100), Create(21, "next.txt", 4200));

        Assert.AreEqual("next.txt", changes.Single().Entry.Name);
        Assert.HasCount(1, fixture.GrowthLines);
        Assert.IsTrue(fixture.Block.Header.IsCapacityExhausted);
        Assert.AreEqual(5000L, fixture.Block.Header.UsnNextUsn);
        var skipped = fixture.DiagnosticsLines.Single(line => line.StartsWith("Skipped journal record", StringComparison.Ordinal));
        StringAssert.Contains(skipped, "USN 4100");
        StringAssert.Contains(skipped, "again right after a growth");
    }

    [TestMethod]
    public async Task ARecordAbove32Bits_IsFlaggedAndSkippedWithoutAGrowth()
    {
        await using var fixture = await OpenAsync();
        var before = fixture.Block;

        var changes = await fixture.ApplyAsync(5000, Create((ulong)uint.MaxValue + 1, "huge.txt", 4100),
            Create(21, "next.txt", 4200));

        Assert.AreEqual("next.txt", changes.Single().Entry.Name);
        Assert.AreSame(before, fixture.Block);
        Assert.IsEmpty(fixture.GrowthLines);
        Assert.IsTrue(fixture.Index.Status.Block!.IsCapacityExhausted);
        Assert.AreEqual(5000L, fixture.Block.Header.UsnNextUsn);
        StringAssert.Contains(fixture.DiagnosticsLines.Single(line => line.StartsWith("Skipped", StringComparison.Ordinal)),
            "above 32 bits");
    }

    /// <summary>
    ///     A lineage sealed before the growth publishes refuses the replacement with <see cref="ObjectDisposedException" />.
    ///     That is a stop, not a capacity failure: the batch ends at the refused record, unflagged and unskipped.
    /// </summary>
    [TestMethod]
    public async Task AGrowthWhoseLineageIsSealedBeforePublication_EndsTheBatchAtTheRefusedRecord()
    {
        await using var fixture = await OpenAsync();
        var before = fixture.Block;
        var lineage = fixture.RootBlock.Mapped;
        fixture.Index.Hooks.WatchGrowthStepReached = (_, step, _) =>
        {
            if (step == FileIndex.WatchGrowthStep.RenamedIntoPlace)
            {
                _ = lineage.Seal();
            }
        };

        var changes = await fixture.ApplyAsync(5000, Create(10, "applied.txt", 4100), Create(100, "far.txt", 4200),
            Create(11, "after.txt", 4300));

        Assert.AreEqual("applied.txt", changes.Single().Entry.Name);
        Assert.AreSame(before, fixture.Block);
        Assert.AreEqual(4200L, before.Header.UsnNextUsn);
        Assert.IsFalse(before.Header.IsCapacityExhausted);
        Assert.IsFalse(fixture.DiagnosticsLines.Any(line => line.StartsWith("Skipped", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ACanonicalGrowth_RenamesTheReplacementIntoTheSlotAndRetiresTheOldFile()
    {
        await using var fixture = await OpenAsync();

        await fixture.ApplyAsync(5000, Create(100, "far.txt", 4200));
        await fixture.Index.GrowthFlushForTest(Volume).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(fixture.CanonicalPath, fixture.Block.Path);
        Assert.IsFalse(fixture.Block.DeletesOnClose);
        Assert.AreEqual(fixture.Block.Length, new FileInfo(fixture.CanonicalPath).Length);
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.growing-*"));
        Assert.HasCount(1, Directory.GetFiles(CacheDirectoryPath, "*.retired-*"),
            "the retired mapping keeps its renamed-aside file until the lineage is released");
        StringAssert.Contains(fixture.GrowthLines.Single(), "Retired mappings hold");
    }

    [TestMethod]
    public async Task ACacheDisabledGrowth_UsesAPrivateDeleteOnCloseReplacement()
    {
        await using var fixture = await OpenAsync(cacheMode: CacheMode.Disabled);
        var before = fixture.Block.Path;

        var changes = await fixture.ApplyAsync(5000, Create(100, "far.txt", 4200));

        Assert.HasCount(1, changes);
        Assert.IsTrue(fixture.Block.DeletesOnClose);
        Assert.AreNotEqual(before, fixture.Block.Path);
        Assert.AreEqual(BlockLayout.PlanCapacities(101, SmallCapacities), WatchGrowthPlan.CapacitiesOf(fixture.Block));
    }

    /// <summary>
    ///     Rows keep their numbers across a growth copy, so a directory view's binding, resolved on the old mapping, names
    ///     the same row on the replacement, and <see cref="FileEntry.Parent" /> walks up to the view's root and stops
    ///     there on both mappings.
    /// </summary>
    [TestMethod]
    public async Task ParentAcrossAGrowth_StopsAtTheDirectoryViewsRoot()
    {
        Directory.CreateDirectory(Path.Combine(TreeRoot, "docs"));
        await using var volume = await OpenAsync();
        await using var view = await FileIndex.OpenAsync(
            volume.Options with { Root = RootKey.FromDirectory(Path.Combine(TreeRoot, "docs")) },
            TestContext.CancellationToken);
        var nested = view.Find(Path.Combine(TreeRoot, "docs", "b.txt"), TestContext.CancellationToken)!.Value;
        var top = nested.Parent!.Value;
        Assert.IsNull(top.Parent, "the view's root is its top");
        var before = view.CurrentBlock!.Block;

        await volume.ApplyAsync(5000, Create(100, "far.txt", 4200), Create(30, "sub", 4300, DirectoryRow),
            Create(31, "c.txt", 4400, 30));

        Assert.AreNotSame(before, view.CurrentBlock!.Block, "the view shares the volume's lineage");
        Assert.AreEqual(DirectoryRow, nested.Parent?.RowIndex);
        Assert.IsNull(top.Parent, "the binding names the same row on the replacement");
        var deep = view.Find(Path.Combine(TreeRoot, "docs", "sub", "c.txt"), TestContext.CancellationToken)!.Value;
        Assert.AreEqual("docs", deep.Parent?.Parent?.Name);
        Assert.IsNull(deep.Parent?.Parent?.Parent);
        Assert.HasCount(1, volume.GrowthLines);
    }
}
