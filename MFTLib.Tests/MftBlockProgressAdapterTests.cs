using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A production's progress reaches the application as a scanning count that never exceeds its expected total and,
///     once <see cref="FileIndex" /> orders it, never decreases, followed by one count-free Indexing sample.
/// </summary>
[TestClass]
public sealed class MftBlockProgressAdapterTests
{
    [TestMethod]
    public void Create_ScanningCountsAboveTheTotal_AreClampedToIt()
    {
        var delivered = new List<IndexScanProgress>();
        var adapter = MftBlockProgressAdapter.Create(Request(delivered))!;

        adapter.Report(BlockWriteProgress.Scanning(5, 10));
        adapter.Report(BlockWriteProgress.Scanning(12, 10));
        adapter.Report(BlockWriteProgress.Scanning(7, null));
        adapter.Report(BlockWriteProgress.Indexing);

        Assert.AreSequenceEqual([5u, 10u, 7u, 0u], delivered.Select(sample => sample.WrittenRowCount).ToArray());
        Assert.AreSequenceEqual([10u, 10u, null, null], delivered.Select(sample => sample.ExpectedRowCount).ToArray());
        Assert.AreSequenceEqual(
            [IndexScanPhase.Scanning, IndexScanPhase.Scanning, IndexScanPhase.Scanning, IndexScanPhase.Indexing],
            delivered.Select(sample => sample.Phase).ToArray());
    }

    [TestMethod]
    public void Create_ThroughTheScanOrder_NeverDecreasesNorExceedsTheTotal()
    {
        var order = new ScanProgressOrder();
        var delivered = new List<IndexScanProgress>();
        var adapter = MftBlockProgressAdapter.Create(Request(delivered, order))!;

        foreach (var (records, total) in new (long, long)[] { (4, 10), (15, 10), (6, 10), (10, 10), (9, 10) })
        {
            adapter.Report(BlockWriteProgress.Scanning(records, total));
        }

        var counts = delivered.Select(sample => sample.WrittenRowCount).ToArray();
        Assert.AreSequenceEqual([4u, 10u, 10u], counts);
        Assert.IsTrue(delivered.All(sample => sample.WrittenRowCount <= sample.ExpectedRowCount));
    }

    static MftBlockProduceRequest Request(List<IndexScanProgress> delivered, ScanProgressOrder? order = null) => new()
    {
        Volume = new VolumeKey('C'),
        VolumeSerial = 1,
        BlockPath = "unused.mlix",
        Progress = new DirectProgress<IndexScanProgress>(sample =>
        {
            if (order?.Admit(sample) ?? true)
            {
                delivered.Add(sample);
            }
        })
    };
}
