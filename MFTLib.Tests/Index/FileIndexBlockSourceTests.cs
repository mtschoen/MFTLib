using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Tracks the origin of each drive's current block across opens and rescans.</summary>
[TestClass]
public class FileIndexBlockSourceTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    FileIndexOptions Options(ProducerPolicy producerPolicy = ProducerPolicy.Enumeration, MftBlockProducer? mftProducer = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = producerPolicy,
            MftProducer = mftProducer
        };
    }

    static BlockFile BuildMftShapedBlock(MftBlockProduceRequest request, ulong journalId, long nextUsn)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(journalId, nextUsn);
            writer.Complete(FixedMoment);
        }

        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
    }

    [TestMethod]
    public async Task Drives_AFirstEverScanReportsTheBlockAsProduced()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task Drives_ASecondOpenReportsTheBlockAsWarmStarted()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task Drives_AfterARescanTheBlockReadsAsProduced()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);

        await reopened.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(BlockSource.ProducedByScan, reopened.Drives.Single().BlockSource);
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
        Assert.AreEqual(BlockSource.None, index.Drives.Single().BlockSource);
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
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        static async Task RebuildWarmStartedDrivesAsync(FileIndex index)
        {
            foreach (var status in index.Drives.Where(
                         drive => drive.BlockSource == BlockSource.WarmStartedFromCache).ToArray())
            {
                await index.RescanAsync(status.DriveLetter, CancellationToken.None);
            }
        }

        await using (var firstOpen = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, CountingProducer),
                         CancellationToken.None))
        {
            Assert.AreEqual(BlockSource.ProducedByScan, firstOpen.Drives.Single().BlockSource);
            await RebuildWarmStartedDrivesAsync(firstOpen);
            Assert.AreEqual(1, invocationCount, "the open already scanned this drive, so the loop must skip it");
        }

        await using var secondOpen = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, CountingProducer),
            CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, secondOpen.Drives.Single().BlockSource);

        await RebuildWarmStartedDrivesAsync(secondOpen);

        Assert.AreEqual(2, invocationCount, "a warm-started drive is the one the loop must rescan");
        Assert.AreEqual(BlockSource.ProducedByScan, secondOpen.Drives.Single().BlockSource);
    }
}
