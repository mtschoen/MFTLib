using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchGrowthFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953, name compaction: a watch growth stores only the names some row references. Nothing outside the
///     block holds a name identifier, so every row, handle and query reads the same before and after, and a query
///     pinned on the old mapping finishes there. A delta growth compacts once unused names reach one eighth of the
///     stored ones, and a refused name region keeps its capacity when compaction leaves it at most half full.
/// </summary>
[TestClass]
public sealed class FileIndexNameCompactionTests : IndexTreeTestBase
{
    const ulong KeptRow = 20;
    const ulong TemporaryRow = 21;

    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    Task<WatchGrowthFixture> OpenAsync(BlockCapacities? capacities = null) =>
        WatchGrowthFixture.OpenAsync(CacheDirectoryPath, TreeRoot, UniqueVolumeSerial, capacities);

    /// <summary>
    ///     Sixteen name changes that fill the 16-entry delta and leave fourteen of twenty stored names unused: a create
    ///     of <c>keep.log</c>, a create of <c>t0.tmp</c> renamed fourteen times, and a delete that tombstones
    ///     <c>docs\b.txt</c>, whose row keeps its name.
    /// </summary>
    static UsnJournalEntry[] UnusedNameHistory()
    {
        var entries = new List<UsnJournalEntry>
        {
            Create(KeptRow, "keep.log", 4100),
            Create(TemporaryRow, "t0.tmp", 4101),
            Entry(NestedFileRow, DirectoryRow, "b.txt", UsnReason.FileDelete | UsnReason.Close, 4102)
        };
        entries.AddRange(Enumerable.Range(1, 14).Select(step => Rename(TemporaryRow, $"t{step}.tmp", 4102 + step)));
        return [.. entries];
    }

    /// <summary>A create the full delta refuses, which a growth applies to the replacement.</summary>
    static UsnJournalEntry Trigger => Create(30, "fresh.dat", 4200);

    static async Task FillDeltaAsync(WatchGrowthFixture fixture)
    {
        await fixture.ApplyAsync(4150, UnusedNameHistory());
        Assert.IsEmpty(fixture.GrowthLines, "the history fits the block");
        Assert.AreEqual(fixture.Block.DeltaCapacity, fixture.Block.DeltaEntryCount, "the history fills the delta");
    }

    static NameUsage UsageOf(BlockFile block)
    {
        using var references = NameReferences.Count(block);
        return references.Usage;
    }

    static string[] StoredNames(BlockFile block) =>
        [.. Enumerable.Range(1, (int)block.NameEntryCount).Select(nameId => NamePool.Read(block, (uint)nameId).ToString())];

    /// <summary>Every named row's name and path, read through a handle on the current mapping.</summary>
    static Dictionary<uint, string> ReadEveryRow(WatchGrowthFixture fixture, uint rowCount)
    {
        var rows = new Dictionary<uint, string>();
        for (var row = 0u; row < rowCount; row++)
        {
            if (fixture.Block.Rows[(int)row].NameId == 0)
            {
                continue;
            }

            var entry = FileEntry.Create(fixture.RootBlock, row);
            rows[row] = $"{entry.Name} | {entry.Path} | deleted {entry.IsDeleted}";
        }

        return rows;
    }

    static readonly SearchQuery[] Queries =
    [
        new("a.txt", NameMatchMode.Exact),
        new("keep.log", NameMatchMode.Exact),
        new("*.txt", NameMatchMode.Glob, IncludeDeleted: true),
        new("*.tmp", NameMatchMode.Glob),
        new("t1"),
        new("T14.TMP", NameMatchMode.Exact),
        new("b.txt", NameMatchMode.Exact, IncludeDeleted: true)
    ];

    uint[][] RunQueries(WatchGrowthFixture fixture) =>
        [.. Queries.Select(query => fixture.Index.Search(query, TestContext.CancellationToken)
            .Select(entry => entry.RowIndex).Order().ToArray())];

    [TestMethod]
    public async Task ACompactingGrowth_EveryRowHandleAndQueryReadsTheSame()
    {
        await using var fixture = await OpenAsync();
        await FillDeltaAsync(fixture);
        var rowCount = fixture.Block.RowCount;
        var rowsBefore = ReadEveryRow(fixture, rowCount);
        var queriesBefore = RunQueries(fixture);
        var held = new[] { WatchGrowthFixture.FileRow, NestedFileRow, (uint)KeptRow, (uint)TemporaryRow }
            .Select(row => FileEntry.Create(fixture.RootBlock, row)).ToArray();
        var heldNames = held.Select(entry => entry.Name).ToArray();
        var storedBefore = fixture.Block.NameEntryCount;
        var retired = fixture.Block;

        var changes = await fixture.ApplyAsync(5000, Trigger);

        Assert.AreEqual("fresh.dat", changes.Single().Entry.Name);
        Assert.AreNotSame(retired, fixture.Block);
        StringAssert.Contains(fixture.GrowthLines.Single(), $"names compacted from {storedBefore} to 6");
        CollectionAssert.AreEquivalent(rowsBefore, ReadEveryRow(fixture, rowCount));
        var queriesAfter = RunQueries(fixture);
        for (var index = 0; index < Queries.Length; index++)
        {
            CollectionAssert.AreEqual(queriesBefore[index], queriesAfter[index], Queries[index].ToString());
        }

        for (var index = 0; index < held.Length; index++)
        {
            Assert.AreEqual(FileEntry.Create(fixture.RootBlock, held[index].RowIndex), held[index]);
            Assert.IsFalse(held[index].IsDisposed);
            Assert.AreEqual(heldNames[index], held[index].Name);
        }

        Assert.AreEqual(0u, UsageOf(fixture.Block).UnusedEntries, "only referenced names are stored");
        Assert.AreEqual(7u, fixture.Block.NameEntryCount, "six referenced names and the trigger's");
        Assert.AreEqual(fixture.Block.NameEntryCount, fixture.Block.PostingBaseNameCount + 1);
        Assert.IsNull(NamePoolIntegrity.Check(fixture.Block));
        Assert.IsNull(PostingIntegrity.Check(fixture.Block));
        Assert.HasCount(1, fixture.Index.Search(new SearchQuery("fresh.dat", NameMatchMode.Exact),
            TestContext.CancellationToken));
        CollectionAssert.DoesNotContain(StoredNames(fixture.Block), "t3.tmp");
    }

    /// <summary>
    ///     A delta growth compacts at one eighth unused and copies verbatim below it. Each history has 16 name changes:
    ///     <paramref name="renames" /> renames of <c>a.txt</c>, each leaving one more unused name, and creates for the
    ///     rest, so 20 names are stored when the trigger is refused.
    /// </summary>
    [TestMethod]
    [DataRow(3, true, DisplayName = "three of twenty unused compacts")]
    [DataRow(2, false, DisplayName = "two of twenty unused copies verbatim")]
    public async Task ADeltaGrowth_CompactsAtTheOneEighthThreshold(int renames, bool compacts)
    {
        await using var fixture = await OpenAsync();
        var renamed = Enumerable.Range(1, renames)
            .Select(step => Rename(WatchGrowthFixture.FileRow, $"x{step}.txt", 4100 + step));
        var created = Enumerable.Range(0, 16 - renames)
            .Select(offset => Create(40 + (ulong)offset, $"c{offset}.txt", 4120 + offset));
        var history = renamed.Concat(created).ToArray();
        await fixture.ApplyAsync(4150, history);
        Assert.AreEqual(20u, fixture.Block.NameEntryCount);
        Assert.AreEqual((uint)renames, UsageOf(fixture.Block).UnusedEntries);
        Assert.IsEmpty(fixture.GrowthLines);

        await fixture.ApplyAsync(5000, Trigger);

        var growth = fixture.GrowthLines.Single();
        StringAssert.Contains(growth, "refused by the Delta region");
        Assert.AreEqual(compacts, growth.Contains("names compacted", StringComparison.Ordinal), growth);
        Assert.AreEqual(compacts ? 0u : (uint)renames, UsageOf(fixture.Block).UnusedEntries);
        Assert.AreEqual(compacts ? 21u - (uint)renames : 21u, fixture.Block.NameEntryCount);
        Assert.AreEqual(compacts, !StoredNames(fixture.Block).Contains("a.txt"));
        Assert.AreEqual($"x{renames}.txt", FileEntry.Create(fixture.RootBlock, WatchGrowthFixture.FileRow).Name);
    }

    /// <summary>
    ///     Renames fill the eight name entries with names no row keeps; compaction leaves the region half full, so it
    ///     keeps its capacity, and the refused rename is stored.
    /// </summary>
    [TestMethod]
    public async Task ANameEntryGrowth_AtMostHalfFullAfterCompaction_KeepsTheEntryCapacity()
    {
        await using var fixture = await OpenAsync(SmallCapacities with { NameEntryCapacity = 8 });
        var renames = Enumerable.Range(1, 5).Select(step => Rename(WatchGrowthFixture.FileRow, $"r{step}.txt", 4100 + step)).ToArray();

        var changes = await fixture.ApplyAsync(5000, renames);

        Assert.HasCount(5, changes);
        StringAssert.Contains(fixture.GrowthLines.Single(), "refused by the NameEntries region");
        Assert.AreEqual(SmallCapacities with { NameEntryCapacity = 8 }, WatchGrowthPlan.CapacitiesOf(fixture.Block));
        Assert.AreEqual(5u, fixture.Block.NameEntryCount);
        Assert.AreEqual("r5.txt", FileEntry.Create(fixture.RootBlock, WatchGrowthFixture.FileRow).Name);
        Assert.HasCount(1, fixture.Index.Search(new SearchQuery("r5.txt", NameMatchMode.Exact),
            TestContext.CancellationToken));
        Assert.IsEmpty(fixture.Index.Search(new SearchQuery("r4.txt", NameMatchMode.Exact),
            TestContext.CancellationToken));
    }

    /// <summary>
    ///     A name refusal is final for the lineage only when its region is at its clamp and the referenced names alone
    ///     fill it. A name too long to store is refused again right after a compacting growth that left room; that skip
    ///     is not a clamp, so the lineage remembers none and a later batch grows again.
    /// </summary>
    [TestMethod]
    public async Task ARefusalAfterAGrowthThatLeftRoom_IsNotRememberedAsALineageClamp()
    {
        await using var fixture = await OpenAsync();

        var first = await fixture.ApplyAsync(4500, Create(20, new string('x', 40000), 4100), Create(21, "n.txt", 4200));
        await fixture.ApplyAsync(5000, Create(22, new string('y', 40000), 4600));

        Assert.AreEqual("n.txt", first.Single().Entry.Name);
        Assert.IsNull(fixture.RootBlock.Mapped.Capacity.ClampFor(WriteRefusal.NameEntries));
        Assert.HasCount(2, fixture.GrowthLines, "each batch's refusal grows again");
        Assert.HasCount(2, fixture.DiagnosticsLines.Where(line =>
            line.Contains("again right after a growth", StringComparison.Ordinal)));
    }

    /// <summary>A row enumeration pinned on the old mapping reads every name there while the replacement is compacted.</summary>
    [TestMethod]
    public async Task ARowEnumerationPinnedOnTheOldMapping_FinishesWithTheSameNames()
    {
        await using var fixture = await OpenAsync();
        await FillDeltaAsync(fixture);
        var before = NamesOfEveryRow(fixture, grow: false, TestContext.CancellationToken);

        var during = NamesOfEveryRow(fixture, grow: true, TestContext.CancellationToken);

        CollectionAssert.AreEqual(before, during);
        StringAssert.Contains(fixture.GrowthLines.Single(), "names compacted");
        CollectionAssert.Contains(NamesOfEveryRow(fixture, grow: false, TestContext.CancellationToken), "fresh.dat");
    }

    /// <summary>Enumerates every row's name, optionally growing once the first row is read; the enumerator is a ref struct, so this blocks.</summary>
    static List<string> NamesOfEveryRow(WatchGrowthFixture fixture, bool grow, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var row in fixture.Index.EnumerateRows(new SearchQuery(null, IncludeDeleted: true), cancellationToken))
        {
            if (grow && names.Count == 0)
            {
                fixture.ApplyAsync(5000, Trigger).WaitAsync(HangGuard, cancellationToken).GetAwaiter().GetResult();
            }

            names.Add(row.Name.ToString());
        }

        return names;
    }

    /// <summary>A stop during a compacting copy deletes the replacement and leaves the old block, its unused names included, published.</summary>
    [TestMethod]
    [DataRow(nameof(FileIndex.WatchGrowthStep.ReplacementCreated))]
    [DataRow(nameof(FileIndex.WatchGrowthStep.Copied))]
    public async Task AStopDuringACompactingCopy_KeepsTheOldBlockPublished(string atStep)
    {
        var at = Enum.Parse<FileIndex.WatchGrowthStep>(atStep);
        await using var fixture = await OpenAsync();
        await FillDeltaAsync(fixture);
        var watch = await fixture.StartWatchingAsync();
        var before = fixture.Block;
        var storedBefore = before.NameEntryCount;
        var stop = new TaskCompletionSource<Task<RootOperationResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = fixture.Index;
        index.Hooks.WatchGrowthStepReached = (_, step, cancellationToken) =>
        {
            if (step == at)
            {
                stop.SetResult(Task.Run(() => index.StopWatchingAsync(CancellationToken.None)));
                Assert.IsTrue(cancellationToken.WaitHandle.WaitOne(HangGuard), "the stop reached the growth");
            }
        };

        _ = watch.QueueBatchAsync([CreateRecord(30, "fresh.dat", 4200)], new SyntheticJournalCursor(JournalIdentifier, 5000));
        var stopped = await stop.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await stopped.WaitAsync(HangGuard, TestContext.CancellationToken)).Outcome);

        Assert.AreSame(before, fixture.Block);
        Assert.AreEqual(storedBefore, fixture.Block.NameEntryCount);
        Assert.AreEqual(4200L, fixture.Block.Header.UsnNextUsn);
        Assert.IsEmpty(fixture.GrowthLines);
        Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.growing-*"));
        Assert.AreEqual("t14.tmp", FileEntry.Create(fixture.RootBlock, (uint)TemporaryRow).Name);
    }
}
