using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="DriveBlockStatus.Generation" />: a drive's change version strictly increases on every
///     applied mutation batch and every rescan, and a warm start adopts the cached block's value.
/// </summary>
[TestClass]
public sealed class FileIndexGenerationTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    static ulong GenerationOf(FileIndex index) => index.Drives.Single().Block!.Generation;

    [TestMethod]
    public async Task OpenAsync_ColdScanWithNoPreviousBlock_StartsAtOne()
    {
        await using var index = await FileIndex.OpenAsync(Fixture.Options(), TestContext.CancellationToken);

        Assert.AreEqual(1ul, GenerationOf(index));
    }

    [TestMethod]
    public async Task RescanAsync_StartsTheReplacementBlockAboveTheSupersededGeneration()
    {
        await using var index = await FileIndex.OpenAsync(Fixture.Options(), TestContext.CancellationToken);

        await index.RescanAsync([new VolumeKey('T')], TestContext.CancellationToken);
        Assert.AreEqual(2ul, GenerationOf(index));

        await index.RescanAsync([new VolumeKey('T')], TestContext.CancellationToken);
        Assert.AreEqual(3ul, GenerationOf(index));
    }

    [TestMethod]
    public async Task OpenAsync_WarmStart_AdoptsTheCachedBlocksGeneration()
    {
        await using (var first = await FileIndex.OpenAsync(Fixture.Options(), TestContext.CancellationToken))
        {
            await first.RescanAsync([new VolumeKey('T')], TestContext.CancellationToken);
            Assert.AreEqual(2ul, GenerationOf(first));
        }

        await using var warm = await FileIndex.OpenAsync(Fixture.Options(), TestContext.CancellationToken);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, warm.Drives.Single().Block!.Origin);
        Assert.AreEqual(2ul, GenerationOf(warm));
    }

    [TestMethod]
    public async Task Generation_IsMonotonicAcrossMutationRescanAndMutation()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        var observed = new List<ulong> { GenerationOf(index) };

        await index.ApplyJournalEntriesAsync('T', [WatchHarness.Create(9, "first.txt")],
            WatchHarness.JournalIdentifier, 600);
        observed.Add(GenerationOf(index));
        await index.RescanAsync([new VolumeKey('T')], TestContext.CancellationToken);
        observed.Add(GenerationOf(index));
        await index.ApplyJournalEntriesAsync('T', [WatchHarness.Create(10, "second.txt")],
            WatchHarness.JournalIdentifier, 700);
        observed.Add(GenerationOf(index));
        await index.RescanAsync([new VolumeKey('T')], TestContext.CancellationToken);
        observed.Add(GenerationOf(index));

        var message = string.Join(",", observed);
        for (var position = 1; position < observed.Count; position++)
        {
            Assert.IsGreaterThan(observed[position - 1], observed[position], message);
        }
    }
}
