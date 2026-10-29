using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class MftBlockGrowthTests : IDisposable
{
    const uint DistinctNameRows = 20000;
    TempDirectory _tempDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => _tempDirectory = new TempDirectory("mftlib-growth");

    public void Dispose() => _tempDirectory.Dispose();

    // The standard dump plans 65536 slots and about 1.5 MB of name pool; 20000 distinct 255-character names need more.
    [TestMethod]
    public async Task DumpProduction_WithMoreDistinctNamesThanPlanned_CompletesWithEveryRow()
    {
        var path = MftDumpFixture.WriteFile(_tempDirectory.Path, MftDumpFixture.Standard(), "input.mft");
        var producer = new MftDumpBlockProducer(path, TimeProvider.System, (_, _, _) => DistinctNames());
        var request = Request(Path.Combine(_tempDirectory.Path, "block.mlix"), deleteOnClose: false);

        var result = await producer.ProduceAsync(request, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.IsFalse(block.Header.IsCapacityExhausted);
        Assert.AreEqual(DistinctNameRows + 6, block.Header.RowCount);
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.AreEqual(block.Path, request.BlockPath);
        Assert.IsTrue(File.Exists(request.BlockPath));
        Assert.AreEqual(new string('n', 247) + "00019999", NamePool.ReadRowName(block, 20005).ToString());
    }

    [TestMethod]
    public void ProduceUntilFit_WhenEntriesRunOut_RerunsOnceWithEntriesAtTheSlotCapacity()
    {
        var requests = new List<MftBlockProduceRequest>();
        var request = Request(Path.Combine(_tempDirectory.Path, "entries.mlix"), deleteOnClose: false);

        using var block = MftBlockGrowth.ProduceUntilFit(request, attempt =>
        {
            requests.Add(attempt);
            var entries = attempt.MinimumCapacities.NameEntryCapacity == 0 ? 32u : attempt.MinimumCapacities.NameEntryCapacity;
            return new MftBlockProduceResult(FilledBlock(attempt, entries), 0);
        }).Block;

        Assert.HasCount(2, requests);
        Assert.AreEqual(new BlockCapacities(0, 64, 0), requests[1].MinimumCapacities);
        Assert.IsFalse(block.Header.IsCapacityExhausted);
        Assert.AreEqual(64u, block.Header.NameEntryCapacity);
    }

    [TestMethod]
    public void ProduceUntilFit_WhenRowsRunOut_KeepsTheFlaggedBlock()
    {
        var calls = 0;
        var request = Request(Path.Combine(_tempDirectory.Path, "rows.mlix"), deleteOnClose: false);

        using var block = MftBlockGrowth.ProduceUntilFit(request, attempt =>
        {
            calls++;
            // aislop-ignore-next-line dotnet/IDISP001 -- the produce result owns the block and ProduceUntilFit returns it
            var created = BlockFile.Create(Options(attempt, slots: 8, entries: 8));
            var writer = new BlockWriter(created);
            for (uint row = 5; row < 8; row++)
            {
                writer.TryWriteRow(row, $"r{row}", new RowColumns(5, RowFlags.InUse, 0, 0, 0, 0));
            }

            Assert.IsFalse(writer.TryWriteRow(100, "overflow", new RowColumns(5, RowFlags.InUse, 0, 0, 0, 0)));
            writer.Complete(DateTime.UtcNow, null);
            return new MftBlockProduceResult(created, 0);
        }).Block;

        Assert.AreEqual(1, calls);
        Assert.IsTrue(block.Header.IsCapacityExhausted);
    }

    // Fills the root and distinct names until the entries are full; a larger capacity fits them all.
    static BlockFile FilledBlock(MftBlockProduceRequest request, uint entries)
    {
        var block = BlockFile.Create(Options(request, slots: 64, entries));
        var writer = new BlockWriter(block);
        for (uint row = 5; row < 5 + 40; row++)
        {
            writer.TryWriteRow(row, $"name-{row}", new RowColumns(5, RowFlags.InUse, 0, 0, 0, 0));
        }

        writer.Complete(DateTime.UtcNow, null);
        return block;
    }

    static BlockFileCreateOptions Options(MftBlockProduceRequest request, uint slots, uint entries) =>
        TestBlocks.Options(request.BlockPath, ProducerKind.Mft, slotCapacity: slots, namePoolCapacity: 65536,
            nameEntryCapacity: entries, volumeSerial: request.VolumeSerial, rootRow: 5,
            deleteOnClose: request.DeleteOnClose, cacheTag: request.CacheTag, scanShape: request.ScanShape) with
        {
            Root = request.Volume
        };

    static MftBlockProduceRequest Request(string blockPath, bool deleteOnClose) => new()
    {
        Volume = 'D',
        VolumeSerial = 0,
        BlockPath = blockPath,
        DeleteOnClose = deleteOnClose
    };

    static IEnumerable<IReadOnlyList<MftRecord>> DistinctNames()
    {
        yield return [MftRecordFactory.Create(new MftRecordTestValues
        {
            RecordNumber = 5, ParentRecordNumber = 5, IsDirectory = true, FileName = "."
        })];
        for (uint start = 0; start < DistinctNameRows; start += 1000)
        {
            var batch = new List<MftRecord>();
            for (var offset = start; offset < start + 1000; offset++)
            {
                batch.Add(MftRecordFactory.Create(new MftRecordTestValues
                {
                    RecordNumber = 6 + offset,
                    ParentRecordNumber = 5,
                    FileName = new string('n', 247) + offset.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)
                }));
            }

            yield return batch;
        }
    }
}
