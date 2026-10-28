using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexMftRescanGrowthTests : IndexTreeTestBase
{
    static readonly DateTime FixedMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow("Rows", 128u, 0u, 0u)]
    [DataRow("Entries", 0u, 128u, 0u)]
    [DataRow("Pool", 0u, 0u, 8192u)]
    public async Task Rescan_AfterOneRegionIsExhausted_RequestsOnlyThatRegionDoubled(string region,
        uint slots, uint entries, uint pool)
    {
        var producer = new RecordingMftProducer(region);
        await using var index = await FileIndex.OpenAsync(Options(producer), TestContext.CancellationToken);

        Assert.AreEqual(default(BlockCapacities), producer.Requests[0].MinimumCapacities);
        Assert.IsTrue(index.Status.Block!.IsCapacityExhausted);

        await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, producer.Requests.Count);
        Assert.AreEqual(new BlockCapacities(slots, entries, pool), producer.Requests[1].MinimumCapacities);
        Assert.IsFalse(index.Status.Block!.IsCapacityExhausted);
    }

    [TestMethod]
    public async Task Rescan_AfterARecordIndexBeyondTheSlots_RequestsTheRowsDoubled()
    {
        var producer = new RecordingMftProducer("RecordIndex");
        await using var index = await FileIndex.OpenAsync(Options(producer), TestContext.CancellationToken);

        Assert.IsLessThan(index.CurrentBlock!.Block.SlotCapacity, index.CurrentBlock.Block.RowCount);
        await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(new BlockCapacities(128, 0, 0), producer.Requests[1].MinimumCapacities);
    }

    [TestMethod]
    public async Task Rescan_OfAHealthyBlock_RequestsNoMinimum()
    {
        var producer = new RecordingMftProducer("Healthy");
        await using var index = await FileIndex.OpenAsync(Options(producer), TestContext.CancellationToken);

        await index.RescanAsync(TestContext.CancellationToken);
        await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(3, producer.Requests.Count);
        Assert.AreEqual(default(BlockCapacities), producer.Requests[2].MinimumCapacities);
    }

    [TestMethod]
    public async Task Rescan_OfAWarmStartedExhaustedBlock_RequestsTheGrowth()
    {
        var producer = new RecordingMftProducer("Entries");
        var options = Options(producer);
        await using (var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken))
        {
            Assert.IsTrue(index.Status.Block!.IsCapacityExhausted);
        }

        await using var warm = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        Assert.AreEqual(1, producer.Requests.Count);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, warm.Status.Block!.Origin);
        Assert.IsTrue(warm.Status.Block.IsCapacityExhausted);

        await warm.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, producer.Requests.Count);
        Assert.AreEqual(new BlockCapacities(0, 128, 0), producer.Requests[1].MinimumCapacities);
    }

    FileIndexOptions Options(RecordingMftProducer producer) =>
        Fixture.Options(producer: ProducerKind.Mft) with
        {
            MftSource = new MftIndexSource(producer.ProduceAsync)
        };

    sealed class RecordingMftProducer(string exhaustedRegion)
    {
        int _productionCount;

        public List<MftBlockProduceRequest> Requests { get; } = [];

        public Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new MftBlockProduceResult(_productionCount++ == 0
                ? CreateFirstBlock(request)
                : CreateHealthyBlock(request), 0));
        }

        BlockFile CreateFirstBlock(MftBlockProduceRequest request)
        {
            if (exhaustedRegion == "Healthy")
            {
                return CreateHealthyBlock(request);
            }

            var options = TestBlocks.Options(request.BlockPath, ProducerKind.Mft, slotCapacity: 64,
                namePoolCapacity: 4096, nameEntryCapacity: 64, volumeSerial: request.VolumeSerial,
                rootRow: 5, deleteOnClose: request.DeleteOnClose, cacheTag: request.CacheTag,
                scanShape: request.ScanShape) with
            {
                Root = request.Volume
            };
            var block = BlockFile.Create(options);
            var writer = new BlockWriter(block);
            WriteRoot(writer);
            switch (exhaustedRegion)
            {
                case "Rows":
                    for (uint row = 6; row < 64; row++)
                    {
                        Assert.IsTrue(writer.TryWriteRow(row, "file", Columns(row)));
                    }

                    Assert.IsFalse(writer.TryWriteRow(64, "overflow", Columns(5)));
                    break;
                case "Entries":
                    for (var index = 1; index < 64; index++)
                    {
                        Assert.IsTrue(writer.TryRenameRow(5, $"name-{index}", 5));
                    }

                    Assert.IsFalse(writer.TryRenameRow(5, "name-overflow", 5));
                    break;
                case "Pool":
                    Assert.IsTrue(writer.TryRenameRow(5, new string('f', 3995), 5));
                    Assert.IsFalse(writer.TryRenameRow(5, "é" + new string('a', 254), 5));
                    break;
                case "RecordIndex":
                    Assert.IsFalse(writer.TryWriteRow(1000, "overflow", Columns(5)));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown exhausted region '{exhaustedRegion}'.");
            }

            return Complete(block, writer);
        }

        static BlockFile CreateHealthyBlock(MftBlockProduceRequest request)
        {
            var location = new MftBlockCapacity.FileLocation(request.Volume, request.BlockPath,
                request.VolumeSerial, request.DeleteOnClose);
            var identity = new BlockContentIdentity(request.CacheTag, request.ScanShape);
            var geometry = new NtfsVolumeInformation(16 * 1024, 1024);
            var block = BlockFile.Create(MftBlockCapacity.CreateOptions(geometry, location, identity,
                request.MinimumCapacities));
            var writer = new BlockWriter(block);
            WriteRoot(writer);
            return Complete(block, writer);
        }

        static void WriteRoot(BlockWriter writer)
        {
            Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(5,
                RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0)));
        }

        static RowColumns Columns(uint parentRow) => new(parentRow, RowFlags.InUse, 0, 0, 0, 0);

        static BlockFile Complete(BlockFile block, BlockWriter writer)
        {
            writer.SetJournalCursor(7, 4096);
            writer.Complete(FixedMoment, null);
            return block;
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
