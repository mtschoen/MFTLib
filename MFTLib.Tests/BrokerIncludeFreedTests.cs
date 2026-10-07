using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The include-freed option crosses the broker protocol into the host's scan source and its row filter, so a
///     scan that asks gets deleted rows and a scan that does not gets none.
/// </summary>
[TestClass]
public class BrokerIncludeFreedTests : BrokerBlockTestBase
{
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Produce_IncludeFreedReachesTheHostScanAndFilter(bool includeFreed)
    {
        var seen = new List<bool>();
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, options, _) =>
        {
            seen.Add(options.IncludeFreed);
            return Batches(options);
        }));

        var result = await ProduceAsync(broker.Process, Request(Target()),
            new BrokerScanOptions { IncludeFreed = includeFreed }).WaitAsync(HangGuard);
        var block = result.Block;

        CollectionAssert.AreEqual(new[] { includeFreed }, seen);
        Assert.AreEqual(includeFreed ? RowFlags.InUse | RowFlags.Tombstone : RowFlags.None, block.Rows[21].Flags);
        Assert.AreEqual(includeFreed ? 6u : 0u, block.Rows[21].ParentRow);
        Assert.AreEqual(2u, block.Header.LiveRowCount, "Only the root and its directory are live.");
        block.Dispose();
    }

    [TestMethod]
    public async Task Produce_ADefaultScanAsksForNoFreedRecords()
    {
        var seen = new List<bool>();
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, options, _) =>
        {
            seen.Add(options.IncludeFreed);
            return Batches(options);
        }));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard);

        CollectionAssert.AreEqual(new[] { false }, seen);
        Assert.AreEqual(RowFlags.None, result.Block.Rows[21].Flags);
        result.Block.Dispose();
    }

    [TestMethod]
    public async Task Produce_AScriptedSourceThatIgnoresTheOptionStillGetsNoFreedRowsWithoutIt()
    {
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _, _) =>
            Batches(new MftRecordScanOptions { IncludeFreed = true })));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard);

        Assert.AreEqual(RowFlags.None, result.Block.Rows[21].Flags,
            "The row filter, not only the source, keeps freed records out of a scan that did not ask.");
        result.Block.Dispose();
    }

    // Root, one live directory and file, then (only when the scan asks) a freed file under the directory.
    static IEnumerable<IReadOnlyList<MftRecord>> Batches(MftRecordScanOptions options)
    {
        yield return
        [
            new MftRecord(5, 5, new MftRecordFields(3, FileAttributes.Directory, 0, 0, 5, 5), "."),
            new MftRecord(6, 5, new MftRecordFields(3, FileAttributes.Directory, 0, 0, 6, 5), "dir")
        ];
        if (options.IncludeFreed)
        {
            yield return
            [
                new MftRecord(21, 6, new MftRecordFields(0, FileAttributes.Normal, 7, 0, 9, 6), "freed.txt")
            ];
        }
    }
}
