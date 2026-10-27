using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="IndexScanPhase.Finished" />'s contract: <see cref="FileIndex" /> reports exactly
///     one per root scan, after the producer's last sample for that root, carrying how the scan
///     ended, for enumeration-backed and MFT-backed volumes, in open and in rescans.
/// </summary>
[TestClass]
public sealed class FileIndexScanFinishedTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    string CreateRoot(string name)
    {
        var root = Path.Combine(TreeRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.md"), "hello");
        return root;
    }

    FileIndexOptions Options(IndexedRoot root, ConcurrentQueue<IndexScanProgress> samples,
        ProducerKind producer = ProducerKind.Enumeration, MftBlockProducer? mftProducer = null)
    {
        return new FileIndexOptions
        {
            Root = root with { Producer = producer },
            CacheDirectory = CacheDirectoryPath,
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(samples.Enqueue)
        };
    }

    /// <summary>The root's Finished sample; asserts the count and that it follows every other sample.</summary>
    static IndexScanProgress FinishedOf(ConcurrentQueue<IndexScanProgress> samples, char driveLetter,
        int expectedFinishedCount = 1)
    {
        var root = samples.Where(sample => sample.Root == new VolumeKey(driveLetter)).ToArray();
        var finished = root.Where(sample => sample.Phase == IndexScanPhase.Finished).ToArray();
        Assert.HasCount(expectedFinishedCount, finished, $"root {driveLetter} finished count");
        Assert.AreEqual(IndexScanPhase.Finished, root[^1].Phase, "Finished follows every other sample");
        Assert.IsTrue(root.Where(sample => sample.Phase != IndexScanPhase.Finished)
            .All(sample => sample.Outcome is null));
        return finished[^1];
    }

    static Task<MftBlockProduceResult> ProduceBlock(MftBlockProduceRequest request)
    {
        request.Progress?.Report(new IndexScanProgress(request.Volume, IndexScanPhase.ParsingMft, 1));
        return Task.FromResult(new MftBlockProduceResult(
            SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment),
            SkippedRecordCount: 0));
    }

    [TestMethod]
    public async Task OpenAsync_ConcurrentEnumerationRoots_EachFinishOnceAfterTheirLastSample()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var roots = new[]
        {
            MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1),
            MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), CreateRoot("second"), 2),
            MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('V'), CreateRoot("third"), 3)
        };

        var opened = await Task.WhenAll(roots.Select(root =>
            FileIndex.OpenAsync(Options(root, samples), TestContext.CancellationToken)));
        try
        {
            foreach (var driveLetter in "TUV")
            {
                var finished = FinishedOf(samples, driveLetter);
                Assert.AreEqual(IndexScanOutcome.Succeeded, finished.Outcome);
                Assert.IsGreaterThan(0u, finished.WrittenRowCount);
                Assert.Contains(sample =>
                    sample.Root == new VolumeKey(driveLetter) && sample.Phase == IndexScanPhase.Enumerating, samples);
            }
        }
        finally
        {
            foreach (var index in opened)
            {
                await index.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task OpenAsync_MftRoot_FinishesAfterProducerSamples()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1);

        await using var index = await FileIndex.OpenAsync(
            Options(root, samples, ProducerKind.Mft, (request, _) => ProduceBlock(request)),
            TestContext.CancellationToken);

        var finished = FinishedOf(samples, 'T');
        Assert.AreEqual(IndexScanOutcome.Succeeded, finished.Outcome);
        Assert.Contains(sample => sample.Phase == IndexScanPhase.ParsingMft, samples);
    }

    [TestMethod]
    public async Task OpenAsync_MftProducerFails_FinishesFailedWithZeroRows()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1);

        await using var index = await FileIndex.OpenAsync(
            Options(root, samples, ProducerKind.Mft,
                (_, _) => throw new InvalidOperationException("producer failed")),
            TestContext.CancellationToken);

        var finished = FinishedOf(samples, 'T');
        Assert.AreEqual(IndexScanOutcome.Failed, finished.Outcome);
        Assert.AreEqual(0u, finished.WrittenRowCount);
    }

    [TestMethod]
    public async Task OpenAsync_CancelledScan_FinishesCancelledOnce()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1);
        var options = Options(root, samples, ProducerKind.Mft,
            (_, _) => throw new OperationCanceledException());
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        });

        Assert.AreEqual(IndexScanOutcome.Cancelled, FinishedOf(samples, 'T').Outcome);
    }

    [TestMethod]
    public async Task RescanAsync_ReportsOneMoreFinishedPerScan()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var rootT = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1);
        var rootU = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), CreateRoot("second"), 2);
        await using var indexT = await FileIndex.OpenAsync(Options(rootT, samples), TestContext.CancellationToken);
        await using var indexU = await FileIndex.OpenAsync(Options(rootU, samples), TestContext.CancellationToken);
        Assert.AreEqual(IndexScanOutcome.Succeeded, FinishedOf(samples, 'T').Outcome);

        await indexT.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(IndexScanOutcome.Succeeded, FinishedOf(samples, 'T', expectedFinishedCount: 2).Outcome);
        Assert.ContainsSingle(sample =>
            sample.Root == new VolumeKey('U') && sample.Phase == IndexScanPhase.Finished, samples);
    }

    sealed class ThrowingOnFinished : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value)
        {
            if (value.Phase == IndexScanPhase.Finished)
            {
                throw new InvalidOperationException("handler boom");
            }
        }
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerThrowsAfterSuccess_RootStillReadyAndFaultLogged()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var options = Options(MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1), new ConcurrentQueue<IndexScanProgress>(),
            ProducerKind.Mft, (request, _) => ProduceBlock(request)) with
        {
            ScanProgress = new ThrowingOnFinished(),
            Diagnostics = diagnostics.Enqueue
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.Contains(line => line.Contains("handler boom"), diagnostics);
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerThrowsAfterCancellation_OriginalCancellationWins()
    {
        var options = Options(MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1), new ConcurrentQueue<IndexScanProgress>(),
            ProducerKind.Mft, (_, _) => throw new OperationCanceledException()) with
        {
            ScanProgress = new ThrowingOnFinished()
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        });
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerAndDiagnosticsBothThrowAfterSuccess_RootReadyWithBlockAdopted()
    {
        var options = Options(MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1), new ConcurrentQueue<IndexScanProgress>(),
            ProducerKind.Mft, (request, _) => ProduceBlock(request)) with
        {
            ScanProgress = new ThrowingOnFinished(),
            Diagnostics = _ => throw new InvalidOperationException("diagnostics boom")
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerAndDiagnosticsBothThrowAfterCancellation_CancellationPreserved()
    {
        var options = Options(MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1), new ConcurrentQueue<IndexScanProgress>(),
            ProducerKind.Mft, (_, _) => throw new OperationCanceledException()) with
        {
            ScanProgress = new ThrowingOnFinished(),
            Diagnostics = _ => throw new InvalidOperationException("diagnostics boom")
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        });
    }
}
