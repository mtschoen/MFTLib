using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Tracks the origin of each root's current block across opens and rescans.</summary>
[TestClass]
public sealed class FileIndexBlockOriginTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    FileIndexOptions Options(ProducerKind producer = ProducerKind.Enumeration, MftBlockProducer? mftProducer = null) =>
        Fixture.Options(producer: SyntheticRoot.SelectionFor(producer)) with
        {
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer)
        };

    [TestMethod]
    public async Task Roots_AFirstEverScanReportsTheBlockAsProduced()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Status.Block!.Origin);
    }

    [TestMethod]
    public async Task Roots_ASecondOpenReportsTheBlockAsWarmStarted()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block!.Origin);
    }

    [TestMethod]
    public async Task Roots_AfterARescanTheBlockReadsAsProduced()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block!.Origin);

        await reopened.RescanAsync(CancellationToken.None);

        Assert.AreEqual(BlockOrigin.ProducedByScan, reopened.Status.Block!.Origin);
    }

    [TestMethod]
    public async Task Roots_AnOfflineRootReportsNoBlockOrigin()
    {
        var options = new FileIndexOptions
        {

            CacheDirectory = CacheDirectoryPath
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('Z'), Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration));

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(RootState.Offline, index.Status.State);
        Assert.IsNull(index.Status.Block);
    }

    /// <summary>
    ///     MFTLib#146's acceptance: a consumer's "build and rebuild everything" loop rescans only
    ///     the roots that warm-started, so a first-ever open scans each root once rather than
    ///     twice, and an open with a cache present still gets exactly one real scan.
    /// </summary>
    [TestMethod]
    public async Task ConsumerRebuildLoop_SkipsRootsTheOpenJustScanned()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment),
                SkippedRecordCount: 0));
        }

        static async Task RebuildWarmStartedRootsAsync(FileIndex index)
        {
            if (index.Status.Block?.Origin == BlockOrigin.WarmStartedFromCache)
            {
                await index.RescanAsync(CancellationToken.None);
            }
        }

        await using (var firstOpen = await FileIndex.OpenAsync(Options(ProducerKind.Mft, CountingProducer),
                         CancellationToken.None))
        {
            Assert.AreEqual(BlockOrigin.ProducedByScan, firstOpen.Status.Block!.Origin);
            await RebuildWarmStartedRootsAsync(firstOpen);
            Assert.AreEqual(1, invocationCount, "the open already scanned this root, so the loop must skip it");
        }

        await using var secondOpen = await FileIndex.OpenAsync(Options(ProducerKind.Mft, CountingProducer),
            CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, secondOpen.Status.Block!.Origin);

        await RebuildWarmStartedRootsAsync(secondOpen);

        Assert.AreEqual(2, invocationCount, "a warm-started root is the one the loop must rescan");
        Assert.AreEqual(BlockOrigin.ProducedByScan, secondOpen.Status.Block!.Origin);
    }
}
