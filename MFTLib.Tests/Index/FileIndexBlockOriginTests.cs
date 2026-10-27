using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Tracks the origin of each drive's current block across opens and rescans.</summary>
[TestClass]
public sealed class FileIndexBlockOriginTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    FileIndexOptions Options(ProducerKind producer = ProducerKind.Enumeration, MftBlockProducer? mftProducer = null) =>
        Fixture.Options(producer: producer) with
        {
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer)
        };

    [TestMethod]
    public async Task Drives_AFirstEverScanReportsTheBlockAsProduced()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Drives.Single().Block!.Origin);
    }

    [TestMethod]
    public async Task Drives_ASecondOpenReportsTheBlockAsWarmStarted()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives.Single().Block!.Origin);
    }

    [TestMethod]
    public async Task Drives_AfterARescanTheBlockReadsAsProduced()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives.Single().Block!.Origin);

        await reopened.RescanAsync([new VolumeKey('T')], CancellationToken.None);

        Assert.AreEqual(BlockOrigin.ProducedByScan, reopened.Drives.Single().Block!.Origin);
    }

    [TestMethod]
    public async Task Drives_AnOfflineDriveReportsNoBlockOrigin()
    {
        var options = new FileIndexOptions
        {
            Drives = [MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('Z'), Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration)],
            CacheDirectory = CacheDirectoryPath
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(DriveState.Offline, index.Drives.Single().State);
        Assert.IsNull(index.Drives.Single().Block);
    }

    /// <summary>
    ///     MFTLib#146's acceptance: a consumer's "build and rebuild everything" loop rescans only
    ///     the drives that warm-started, so a first-ever open scans each drive once rather than
    ///     twice, and an open with a cache present still gets exactly one real scan.
    /// </summary>
    [TestMethod]
    public async Task ConsumerRebuildLoop_SkipsDrivesTheOpenJustScanned()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment),
                SkippedRecordCount: 0));
        }

        static async Task RebuildWarmStartedDrivesAsync(FileIndex index)
        {
            foreach (var status in index.Drives.Where(
                         drive => drive.Block!.Origin == BlockOrigin.WarmStartedFromCache).ToArray())
            {
                await index.RescanAsync([status.Drive], CancellationToken.None);
            }
        }

        await using (var firstOpen = await FileIndex.OpenAsync(Options(ProducerKind.Mft, CountingProducer),
                         CancellationToken.None))
        {
            Assert.AreEqual(BlockOrigin.ProducedByScan, firstOpen.Drives.Single().Block!.Origin);
            await RebuildWarmStartedDrivesAsync(firstOpen);
            Assert.AreEqual(1, invocationCount, "the open already scanned this drive, so the loop must skip it");
        }

        await using var secondOpen = await FileIndex.OpenAsync(Options(ProducerKind.Mft, CountingProducer),
            CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, secondOpen.Drives.Single().Block!.Origin);

        await RebuildWarmStartedDrivesAsync(secondOpen);

        Assert.AreEqual(2, invocationCount, "a warm-started drive is the one the loop must rescan");
        Assert.AreEqual(BlockOrigin.ProducedByScan, secondOpen.Drives.Single().Block!.Origin);
    }
}
