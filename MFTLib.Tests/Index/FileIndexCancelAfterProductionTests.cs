using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Mirrors git-wizard's CancellationAfterRescanProduction_PreservesSnapshotAndWatch: the caller's
///     token is cancelled from inside the producer, after the block is written and before the index
///     publishes it. The cancelled rescan must leave the old snapshot and the running watch in place.
/// </summary>
[TestClass]
public sealed class FileIndexCancelAfterProductionTests
{
    const uint VolumeSerial = 0x5EED;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public async Task Rescan_CallerCancelsInsideProducer_PreservesSnapshot(int iterations)
    {
        using var fixture = new IndexTreeFixture(seedDocuments: false);
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        var cancelOnProduce = new Queue<Action>();
        var productions = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            productions++;
            var produced = SeededBlocks.Produce(request, 7, 4096, SeededBlocks.SeededMoment);
            if (cancelOnProduce.TryDequeue(out var cancel))
            {
                cancel();
            }

            return produced;
        }

        var options = fixture.Options(volumeSerial: VolumeSerial, producer: ProducerSelection.MftOnly) with
        {
            MftSource = new MftIndexSource(Produce, source.IndexSource)
        };
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        await index.StartWatchingAsync(TestContext.CancellationToken);
        var committed = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            var original = index.Root(TestContext.CancellationToken).RootBlock.Block;
            cancelOnProduce.Enqueue(cancellation.Cancel);
            try
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                    () => index.RescanAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                cancelOnProduce.Clear();
            }

            if (!ReferenceEquals(original, index.Root(TestContext.CancellationToken).RootBlock.Block))
            {
                committed++;
            }

            await index.RescanAsync(TestContext.CancellationToken);
            await index.StartWatchingAsync(TestContext.CancellationToken);
        }

        Assert.AreEqual(0, committed, "cancelled rescans that replaced the snapshot (productions " + productions + ")");
    }
}
