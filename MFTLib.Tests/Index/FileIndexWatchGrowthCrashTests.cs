using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchGrowthFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: a process killed at each step of a watch growth. The test copies the cache directory at the
///     step, which is what the disk holds when the process dies there (mapped pages live in the page cache), lets the
///     growth finish, and opens a new index over the copy. Before the renames the slot holds the old block at the
///     refused record's cursor; between them it holds no file, so the open cold-scans; after them it holds the
///     replacement at that cursor. The open's sweep removes every sibling.
/// </summary>
[TestClass]
public sealed class FileIndexWatchGrowthCrashTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    [TestMethod]
    [DataRow(nameof(FileIndex.WatchGrowthStep.ReplacementCreated), "old", DisplayName = "killed after the replacement is created")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.Copied), "old", DisplayName = "killed after the copy")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.RenamedAside), "none", DisplayName = "killed between the renames")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.RenamedIntoPlace), "grown", DisplayName = "killed after the renames")]
    [DataRow(nameof(FileIndex.WatchGrowthStep.Published), "grown", DisplayName = "killed after publication")]
    public async Task AKillAtEachStep_ReopensToTheDocumentedOutcome(string atStep, string slot)
    {
        var at = Enum.Parse<FileIndex.WatchGrowthStep>(atStep);
        using var killed = new TempDirectory("mftlib-growth-killed");
        var snapshot = Path.Combine(killed.Path, "cache");
        await using (var fixture = await WatchGrowthFixture.OpenAsync(CacheDirectoryPath, TreeRoot, UniqueVolumeSerial))
        {
            var cacheDirectory = fixture.CacheDirectory;
            fixture.Index.Hooks.WatchGrowthStepReached = (_, step, _) =>
            {
                if (step == at)
                {
                    SnapshotCacheTo(cacheDirectory, snapshot);
                }
            };

            await fixture.ApplyAsync(5000, Create(10, "applied.txt", 4100), Create(100, "far.txt", 4200));
        }

        var siblingsAtTheKill = Directory.GetFiles(snapshot).Select(Path.GetFileName).ToArray();
        await using var reopened = await WatchGrowthFixture.OpenAsync(snapshot, TreeRoot, UniqueVolumeSerial);
        var block = reopened.Index.Status.Block!;

        switch (slot)
        {
            case "old":
                Assert.IsTrue(siblingsAtTheKill.Any(name => name!.Contains(".growing-", StringComparison.Ordinal)));
                Assert.AreEqual(BlockOrigin.WarmStartedFromCache, block.Origin);
                Assert.AreEqual(SmallCapacities, WatchGrowthPlan.CapacitiesOf(reopened.Block));
                Assert.AreEqual(4200L, reopened.Block.Header.UsnNextUsn, "the old block holds the refused record's cursor");
                Assert.IsNotNull(reopened.Index.Find(Path.Combine(TreeRoot, "applied.txt"), TestContext.CancellationToken));
                break;
            case "none":
                Assert.HasCount(2, siblingsAtTheKill, "only the renamed-aside file and the replacement were on disk");
                Assert.AreEqual(BlockOrigin.ProducedByScan, block.Origin, "no canonical file, so the open cold-scans");
                Assert.AreEqual(StartUsn, reopened.Block.Header.UsnNextUsn);
                break;
            default:
                Assert.IsTrue(siblingsAtTheKill.Any(name => name!.Contains(".retired-", StringComparison.Ordinal)));
                Assert.AreEqual(BlockOrigin.WarmStartedFromCache, block.Origin);
                Assert.AreEqual(BlockLayout.PlanCapacities(101, SmallCapacities), WatchGrowthPlan.CapacitiesOf(reopened.Block));
                Assert.AreEqual(4200L, reopened.Block.Header.UsnNextUsn, "replay from the refused record is exact");
                Assert.IsNull(reopened.Index.Find(Path.Combine(TreeRoot, "far.txt"), TestContext.CancellationToken),
                    "the refused record is replayed, not already applied");
                break;
        }

        Assert.IsEmpty(Directory.GetFiles(snapshot, "*.growing-*"));
        Assert.IsEmpty(Directory.GetFiles(snapshot, "*.retired-*"));
    }
}
