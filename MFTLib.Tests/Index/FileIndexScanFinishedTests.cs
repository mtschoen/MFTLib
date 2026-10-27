using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="IndexScanPhase.Finished" />'s contract: <see cref="FileIndex" /> reports exactly
///     one per drive scan, after the producer's last sample for that drive, carrying how the scan
///     ended, for enumeration-backed and MFT-backed drives, in open and in rescans.
/// </summary>
[TestClass]
public sealed class FileIndexScanFinishedTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    string CreateDriveRoot(string name)
    {
        var root = Path.Combine(TreeRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.md"), "hello");
        return root;
    }

    FileIndexOptions Options(IReadOnlyList<IndexedDrive> drives, ConcurrentQueue<IndexScanProgress> samples,
        ProducerKind producer = ProducerKind.Enumeration, MftBlockProducer? mftProducer = null)
    {
        return new FileIndexOptions
        {
            Drives = drives.Select(drive => drive with { Producer = producer }).ToArray(),
            CacheDirectory = CacheDirectoryPath,
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(samples.Enqueue)
        };
    }

    /// <summary>The drive's Finished sample; asserts the count and that it follows every other sample.</summary>
    static IndexScanProgress FinishedOf(ConcurrentQueue<IndexScanProgress> samples, char driveLetter,
        int expectedFinishedCount = 1)
    {
        var drive = samples.Where(sample => sample.Drive == driveLetter).ToArray();
        var finished = drive.Where(sample => sample.Phase == IndexScanPhase.Finished).ToArray();
        Assert.HasCount(expectedFinishedCount, finished, $"drive {driveLetter} finished count");
        Assert.AreEqual(IndexScanPhase.Finished, drive[^1].Phase, "Finished follows every other sample");
        Assert.IsTrue(drive.Where(sample => sample.Phase != IndexScanPhase.Finished)
            .All(sample => sample.Outcome is null));
        return finished[^1];
    }

    static Task<MftBlockProduceResult> ProduceBlock(MftBlockProduceRequest request)
    {
        request.Progress?.Report(new IndexScanProgress(request.Drive, IndexScanPhase.ParsingMft, 1));
        return Task.FromResult(new MftBlockProduceResult(
            SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment),
            SkippedRecordCount: 0));
    }

    [TestMethod]
    public async Task OpenAsync_ConcurrentEnumerationDrives_EachFinishOnceAfterTheirLastSample()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var drives = new[]
        {
            new IndexedDrive('T', CreateDriveRoot("first"), 1),
            new IndexedDrive('U', CreateDriveRoot("second"), 2),
            new IndexedDrive('V', CreateDriveRoot("third"), 3)
        };

        await using var index = await FileIndex.OpenAsync(Options(drives, samples),
            TestContext.CancellationToken);

        foreach (var driveLetter in "TUV")
        {
            var finished = FinishedOf(samples, driveLetter);
            Assert.AreEqual(IndexScanOutcome.Succeeded, finished.Outcome);
            Assert.IsGreaterThan(0u, finished.WrittenRowCount);
            Assert.Contains(sample =>
                sample.Drive == driveLetter && sample.Phase == IndexScanPhase.Enumerating, samples);
        }
    }

    [TestMethod]
    public async Task OpenAsync_MftDrive_FinishesAfterProducerSamples()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };

        await using var index = await FileIndex.OpenAsync(
            Options(drives, samples, ProducerKind.Mft, (request, _) => ProduceBlock(request)),
            TestContext.CancellationToken);

        var finished = FinishedOf(samples, 'T');
        Assert.AreEqual(IndexScanOutcome.Succeeded, finished.Outcome);
        Assert.Contains(sample => sample.Phase == IndexScanPhase.ParsingMft, samples);
    }

    [TestMethod]
    public async Task OpenAsync_MftProducerFails_FinishesFailedWithZeroRows()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };

        await using var index = await FileIndex.OpenAsync(
            Options(drives, samples, ProducerKind.Mft,
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
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };
        var options = Options(drives, samples, ProducerKind.Mft,
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
        var drives = new[]
        {
            new IndexedDrive('T', CreateDriveRoot("first"), 1),
            new IndexedDrive('U', CreateDriveRoot("second"), 2)
        };
        await using var index = await FileIndex.OpenAsync(Options(drives, samples),
            TestContext.CancellationToken);
        Assert.AreEqual(IndexScanOutcome.Succeeded, FinishedOf(samples, 'T').Outcome);

        await index.RescanAsync(['T'], TestContext.CancellationToken);

        Assert.AreEqual(IndexScanOutcome.Succeeded, FinishedOf(samples, 'T', expectedFinishedCount: 2).Outcome);
        Assert.ContainsSingle(sample =>
            sample.Drive == 'U' && sample.Phase == IndexScanPhase.Finished, samples);
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
    public async Task OpenAsync_FinishedHandlerThrowsAfterSuccess_DriveStillReadyAndFaultLogged()
    {
        var diagnostics = new ConcurrentQueue<string>();
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
            ProducerKind.Mft, (request, _) => ProduceBlock(request)) with
        {
            ScanProgress = new ThrowingOnFinished(),
            Diagnostics = diagnostics.Enqueue
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.Contains(line => line.Contains("handler boom"), diagnostics);
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerThrowsAfterCancellation_OriginalCancellationWins()
    {
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
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
    public async Task OpenAsync_FinishedHandlerAndDiagnosticsBothThrowAfterSuccess_DriveReadyWithBlockAdopted()
    {
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
            ProducerKind.Mft, (request, _) => ProduceBlock(request)) with
        {
            ScanProgress = new ThrowingOnFinished(),
            Diagnostics = _ => throw new InvalidOperationException("diagnostics boom")
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerAndDiagnosticsBothThrowAfterCancellation_CancellationPreserved()
    {
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
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
