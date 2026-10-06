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
public class FileIndexScanFinishedTests
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
        Directory.CreateDirectory(_treeRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    string CreateDriveRoot(string name)
    {
        var root = Path.Combine(_treeRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.md"), "hello");
        return root;
    }

    FileIndexOptions Options(IReadOnlyList<IndexedDrive> drives, ConcurrentQueue<IndexScanProgress> samples,
        ProducerPolicy producerPolicy = ProducerPolicy.Enumeration, MftBlockProducer? mftProducer = null)
    {
        return new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = producerPolicy,
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer),
            Progress = new SynchronousProgress<IndexScanProgress>(samples.Enqueue)
        };
    }

    /// <summary>The drive's Finished sample; asserts the count and that it follows every other sample.</summary>
    static IndexScanProgress FinishedOf(ConcurrentQueue<IndexScanProgress> samples, char driveLetter,
        int expectedFinishedCount = 1)
    {
        var drive = samples.Where(sample => sample.DriveLetter == driveLetter).ToArray();
        var finished = drive.Where(sample => sample.Phase == IndexScanPhase.Finished).ToArray();
        Assert.AreEqual(expectedFinishedCount, finished.Length, $"drive {driveLetter} finished count");
        Assert.AreEqual(IndexScanPhase.Finished, drive[^1].Phase, "Finished follows every other sample");
        Assert.IsTrue(drive.Where(sample => sample.Phase != IndexScanPhase.Finished)
            .All(sample => sample.Outcome is null));
        return finished[^1];
    }

    static Task<MftBlockProduceResult> ProduceBlock(MftBlockProduceRequest request)
    {
        request.Progress?.Report(new IndexScanProgress(request.DriveLetter, IndexScanPhase.ParsingMft, 1));
        return Task.FromResult(new MftBlockProduceResult(
            SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment),
            JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
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
            TestContext.CancellationTokenSource.Token);

        foreach (var driveLetter in "TUV")
        {
            var finished = FinishedOf(samples, driveLetter);
            Assert.AreEqual(IndexScanOutcome.Succeeded, finished.Outcome);
            Assert.IsTrue(finished.RowsWritten > 0);
            Assert.IsTrue(samples.Any(sample =>
                sample.DriveLetter == driveLetter && sample.Phase == IndexScanPhase.Enumerating));
        }
    }

    [TestMethod]
    public async Task OpenAsync_MftDrive_FinishesAfterProducerSamples()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };

        await using var index = await FileIndex.OpenAsync(
            Options(drives, samples, ProducerPolicy.Mft, (request, _) => ProduceBlock(request)),
            TestContext.CancellationTokenSource.Token);

        var finished = FinishedOf(samples, 'T');
        Assert.AreEqual(IndexScanOutcome.Succeeded, finished.Outcome);
        Assert.IsTrue(samples.Any(sample => sample.Phase == IndexScanPhase.ParsingMft));
    }

    [TestMethod]
    public async Task OpenAsync_MftProducerFails_FinishesFailedWithZeroRows()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };

        await using var index = await FileIndex.OpenAsync(
            Options(drives, samples, ProducerPolicy.Mft,
                (_, _) => throw new InvalidOperationException("producer failed")),
            TestContext.CancellationTokenSource.Token);

        var finished = FinishedOf(samples, 'T');
        Assert.AreEqual(IndexScanOutcome.Failed, finished.Outcome);
        Assert.AreEqual(0u, finished.RowsWritten);
    }

    [TestMethod]
    public async Task OpenAsync_CancelledScan_FinishesCancelledOnce()
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };
        var options = Options(drives, samples, ProducerPolicy.Mft,
            (_, _) => throw new OperationCanceledException());
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);
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
            TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(IndexScanOutcome.Succeeded, FinishedOf(samples, 'T').Outcome);

        await index.RescanAsync('T', TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(IndexScanOutcome.Succeeded, FinishedOf(samples, 'T', expectedFinishedCount: 2).Outcome);
        Assert.AreEqual(1, samples.Count(sample =>
            sample.DriveLetter == 'U' && sample.Phase == IndexScanPhase.Finished));
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
            ProducerPolicy.Mft, (request, _) => ProduceBlock(request)) with
        {
            Progress = new ThrowingOnFinished(),
            Diagnostics = diagnostics.Enqueue
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.IsTrue(diagnostics.Any(line => line.Contains("handler boom")));
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerThrowsAfterCancellation_OriginalCancellationWins()
    {
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
            ProducerPolicy.Mft, (_, _) => throw new OperationCanceledException()) with
        {
            Progress = new ThrowingOnFinished()
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);
        });
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerAndDiagnosticsBothThrowAfterSuccess_DriveReadyWithBlockAdopted()
    {
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
            ProducerPolicy.Mft, (request, _) => ProduceBlock(request)) with
        {
            Progress = new ThrowingOnFinished(),
            Diagnostics = _ => throw new InvalidOperationException("diagnostics boom")
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockSource.ProducedByScan, status.BlockSource);
    }

    [TestMethod]
    public async Task OpenAsync_FinishedHandlerAndDiagnosticsBothThrowAfterCancellation_CancellationPreserved()
    {
        var options = Options([new IndexedDrive('T', CreateDriveRoot("first"), 1)], new ConcurrentQueue<IndexScanProgress>(),
            ProducerPolicy.Mft, (_, _) => throw new OperationCanceledException()) with
        {
            Progress = new ThrowingOnFinished(),
            Diagnostics = _ => throw new InvalidOperationException("diagnostics boom")
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);
        });
    }
}
