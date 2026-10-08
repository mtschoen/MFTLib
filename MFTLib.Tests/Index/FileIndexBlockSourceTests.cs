using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Tracks the origin of each drive's current block across opens and rescans.</summary>
[TestClass]
public class FileIndexBlockSourceTests
{
    OwnedIndexDirectories _directories = null!;
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(ProducerPolicy producerPolicy = ProducerPolicy.Enumeration, MftBlockProducer? mftProducer = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = producerPolicy,
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer)
        };
    }

    [TestMethod]
    public async Task Drives_AFirstEverScanReportsTheBlockAsProduced()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().Block.Source);
    }

    [TestMethod]
    public async Task Drives_ASecondOpenReportsTheBlockAsWarmStarted()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().Block.Source);
    }

    [TestMethod]
    public async Task Drives_AfterARescanTheBlockReadsAsProduced()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().Block.Source);

        await reopened.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(BlockSource.ProducedByScan, reopened.Drives.Single().Block.Source);
    }

    [TestMethod]
    public async Task Drives_AnOfflineDriveReportsNoBlockSource()
    {
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(DriveState.Offline, index.Drives.Single().State);
        Assert.AreEqual(BlockSource.None, index.Drives.Single().Block.Source);
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
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
        }

        static async Task RebuildWarmStartedDrivesAsync(FileIndex index)
        {
            foreach (var status in index.Drives.Where(
                         drive => drive.Block.Source == BlockSource.WarmStartedFromCache).ToArray())
            {
                await index.RescanAsync(status.DriveLetter, CancellationToken.None);
            }
        }

        await using (var firstOpen = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, CountingProducer),
                         CancellationToken.None))
        {
            Assert.AreEqual(BlockSource.ProducedByScan, firstOpen.Drives.Single().Block.Source);
            await RebuildWarmStartedDrivesAsync(firstOpen);
            Assert.AreEqual(1, invocationCount, "the open already scanned this drive, so the loop must skip it");
        }

        await using var secondOpen = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, CountingProducer),
            CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, secondOpen.Drives.Single().Block.Source);

        await RebuildWarmStartedDrivesAsync(secondOpen);

        Assert.AreEqual(2, invocationCount, "a warm-started drive is the one the loop must rescan");
        Assert.AreEqual(BlockSource.ProducedByScan, secondOpen.Drives.Single().Block.Source);
    }
}
