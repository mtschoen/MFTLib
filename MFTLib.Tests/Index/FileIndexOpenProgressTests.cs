using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndexOptions.OpenProgress" />'s contract: one report per configured drive
///     from <see cref="FileIndex.OpenAsync" />, in configured order, after that drive settles,
///     whatever the outcome. <see cref="FileIndex.RescanAsync" /> stays silent.
/// </summary>
[TestClass]
public class FileIndexOpenProgressTests
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
        Directory.CreateDirectory(_treeRoot);
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

    string CreateDriveRoot(string name)
    {
        var root = Path.Combine(_treeRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.md"), "hello");
        return root;
    }

    FileIndexOptions Options(IReadOnlyList<IndexedDrive> drives,
        IProgress<IndexDriveOpened>? openProgress = null, bool cacheOnly = false,
        ProducerPolicy producerPolicy = ProducerPolicy.Enumeration, MftBlockProducer? mftProducer = null)
    {
        return new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = _cacheDirectory,
            InitialOpenCacheOnly = cacheOnly,
            ProducerPolicy = producerPolicy,
            MftProducer = mftProducer,
            OpenProgress = openProgress
        };
    }

    /// <summary>
    ///     The same fake MFT producer block <see cref="FileIndexProducerSelectionTests" /> uses:
    ///     a small MFT-shaped block written at the request's path with the cursor stamped before
    ///     completion, reopened as a fresh handle. Kept as a local copy, matching how
    ///     FileIndexBlockSourceTests carries its own, so a failure points at one component.
    /// </summary>
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

    static void AssertReport(IndexDriveOpened report, char driveLetter, int ordinal, int total,
        BlockSource blockSource, DriveState state)
    {
        Assert.AreEqual(driveLetter, report.DriveLetter);
        Assert.AreEqual(ordinal, report.Ordinal);
        Assert.AreEqual(total, report.Total);
        Assert.AreEqual(blockSource, report.BlockSource);
        Assert.AreEqual(state, report.State);
    }

    [TestMethod]
    public async Task OpenAsync_CacheOnlyWarmStart_ReportsEveryDriveInConfiguredOrder()
    {
        var drives = new[]
        {
            new IndexedDrive('T', CreateDriveRoot("first"), 1),
            new IndexedDrive('U', CreateDriveRoot("second"), 2),
            new IndexedDrive('V', CreateDriveRoot("third"), 3)
        };

        // A first ordinary open builds each drive's cache block; disposing it leaves the
        // .mlix files behind for the cache-only reopen to warm-start from.
        await using (await FileIndex.OpenAsync(Options(drives), TestContext.CancellationTokenSource.Token))
        {
        }

        var reports = new List<IndexDriveOpened>();
        var options = Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Add), cacheOnly: true);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(3, reports.Count);
        AssertReport(reports[0], 'T', 1, 3, BlockSource.WarmStartedFromCache, DriveState.Ready);
        AssertReport(reports[1], 'U', 2, 3, BlockSource.WarmStartedFromCache, DriveState.Ready);
        AssertReport(reports[2], 'V', 3, 3, BlockSource.WarmStartedFromCache, DriveState.Ready);
    }

    [TestMethod]
    public async Task OpenAsync_CacheOnlyDeclinedDrive_ReportsItsBlocklessState()
    {
        var warmDrive = new IndexedDrive('T', CreateDriveRoot("first"), 1);
        var coldDrive = new IndexedDrive('U', CreateDriveRoot("second"), 2);

        await using (await FileIndex.OpenAsync(Options([warmDrive]), TestContext.CancellationTokenSource.Token))
        {
        }

        var reports = new List<IndexDriveOpened>();
        var options = Options([warmDrive, coldDrive], new SynchronousProgress<IndexDriveOpened>(reports.Add),
            cacheOnly: true);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(2, reports.Count);
        AssertReport(reports[0], 'T', 1, 2, BlockSource.WarmStartedFromCache, DriveState.Ready);
        AssertReport(reports[1], 'U', 2, 2, BlockSource.None, DriveState.Failed);
        Assert.AreEqual(DriveState.Failed, index.Drives.Single(drive => drive.DriveLetter == 'U').State);
    }

    [TestMethod]
    public async Task OpenAsync_ColdScan_ReportsAfterTheProducerCompletes()
    {
        var reports = new List<IndexDriveOpened>();
        var reportCountAtProduceTime = -1;

        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            var result = new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false);
            reportCountAtProduceTime = reports.Count;
            return Task.FromResult(result);
        }

        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };
        var options = Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Add),
            producerPolicy: ProducerPolicy.Mft, mftProducer: FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, reportCountAtProduceTime, "the drive's report must follow its producer's completion");
        Assert.AreEqual(1, reports.Count);
        AssertReport(reports[0], 'T', 1, 1, BlockSource.ProducedByScan, DriveState.Ready);
    }

    [TestMethod]
    public async Task OpenAsync_OfflineDrive_ReportsOfflineWithNoBlock()
    {
        var drives = new[] { new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1) };
        var reports = new List<IndexDriveOpened>();

        await using var index = await FileIndex.OpenAsync(
            Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Add)),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, reports.Count);
        AssertReport(reports[0], 'Z', 1, 1, BlockSource.None, DriveState.Offline);
    }

    [TestMethod]
    public async Task OpenAsync_NullOpenProgress_OpensExactlyAsBefore()
    {
        var drives = new[]
        {
            new IndexedDrive('T', CreateDriveRoot("first"), 1),
            new IndexedDrive('U', CreateDriveRoot("second"), 2)
        };
        var options = Options(drives);
        Assert.IsNull(options.OpenProgress, "the member defaults to null so existing consumers compile unchanged");

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(2, index.Drives.Count);
        Assert.IsTrue(index.Drives.All(drive => drive.State == DriveState.Ready));
        Assert.IsTrue(index.Drives.All(drive => drive.BlockSource == BlockSource.ProducedByScan));
    }

    [TestMethod]
    public async Task RescanAsync_DoesNotReportOpenProgress()
    {
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };
        var reports = new List<IndexDriveOpened>();

        await using var index = await FileIndex.OpenAsync(
            Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Add)),
            TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(1, reports.Count);

        await index.RescanAsync('T', TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, reports.Count, "a rescan has its own awaitable surface and stays silent on open progress");
    }

    [TestMethod]
    public async Task OpenAsync_InterleavedDrives_ReportsSettledStateInOrder()
    {
        var warmDrive = new IndexedDrive('T', CreateDriveRoot("first"), 1);
        var coldDrive = new IndexedDrive('U', CreateDriveRoot("second"), 2);
        var offlineDrive = new IndexedDrive('V', Path.Combine(_treeRoot, "absent"), 3);

        // Pre-create cache for drive T
        await using (await FileIndex.OpenAsync(Options([warmDrive]), TestContext.CancellationTokenSource.Token))
        {
        }

        var reports = new List<IndexDriveOpened>();
        var options = Options([warmDrive, coldDrive, offlineDrive],
            new SynchronousProgress<IndexDriveOpened>(reports.Add));
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(3, reports.Count);
        AssertReport(reports[0], 'T', 1, 3, BlockSource.WarmStartedFromCache, DriveState.Ready);
        AssertReport(reports[1], 'U', 2, 3, BlockSource.ProducedByScan, DriveState.Ready);
        AssertReport(reports[2], 'V', 3, 3, BlockSource.None, DriveState.Offline);

        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, index.Drives[0].BlockSource);
        Assert.AreEqual(DriveState.Ready, index.Drives[1].State);
        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives[1].BlockSource);
        Assert.AreEqual(DriveState.Offline, index.Drives[2].State);
        Assert.AreEqual(BlockSource.None, index.Drives[2].BlockSource);
    }

    [TestMethod]
    public async Task OpenAsync_AsynchronousHandledProducerFailure_ReportsFailedState()
    {
        var reports = new List<IndexDriveOpened>();

        async Task<MftBlockProduceResult> FailingProducer(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("simulated MFT producer failure");
        }

        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("failing"), 1) };
        var options = Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Add),
            producerPolicy: ProducerPolicy.Mft, mftProducer: FailingProducer);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, reports.Count);
        AssertReport(reports[0], 'T', 1, 1, BlockSource.None, DriveState.Failed);
        Assert.AreEqual(DriveState.Failed, index.Drives[0].State);
        StringAssert.Contains(index.Drives[0].MftProducerFailureMessage, "simulated MFT producer failure");
    }

    [TestMethod]
    public async Task OpenAsync_ThrowingOpenProgressCallback_ReleasesUnpublishedBlocks()
    {
        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("throwing"), 1) };
        var throwingProgress = new SynchronousProgress<IndexDriveOpened>(_ =>
            throw new InvalidOperationException("simulated progress callback failure"));
        var options = Options(drives, throwingProgress);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual("simulated progress callback failure", exception.Message);
    }
}
