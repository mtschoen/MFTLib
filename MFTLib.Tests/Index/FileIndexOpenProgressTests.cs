using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndexOptions.OpenProgress" />'s contract: one report per configured drive
///     from <see cref="FileIndex.OpenAsync" />, after that drive settles, whatever the outcome,
///     numbered by <see cref="IndexDriveOpened.SettledCount" /> in settle order. Drives settle
///     concurrently, so these cases assert on each drive's report by letter and on the set of
///     counts, never on which drive settled first. <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> stays silent.
/// </summary>
[TestClass]
public class FileIndexOpenProgressTests
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

    static void AssertReport(IndexDriveOpened report, char driveLetter, int total,
        BlockSource blockSource, DriveState state)
    {
        Assert.AreEqual(driveLetter, report.DriveLetter);
        Assert.AreEqual(total, report.Total);
        Assert.AreEqual(blockSource, report.BlockSource);
        Assert.AreEqual(state, report.State);
    }

    /// <summary>The one report a drive made, and proof that the counts are exactly 1 to the total.</summary>
    static IndexDriveOpened ReportOf(ConcurrentQueue<IndexDriveOpened> reports, char driveLetter)
    {
        var settledCounts = reports.Select(report => report.SettledCount).Order().ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, reports.Count).ToArray(), settledCounts,
            "every settle takes the next count once");
        return reports.Single(report => report.DriveLetter == driveLetter);
    }

    [TestMethod]
    public async Task OpenAsync_CacheOnlyWarmStart_ReportsEveryDriveOnceWithSettledCounts()
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

        var reports = new ConcurrentQueue<IndexDriveOpened>();
        var options = Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Enqueue), cacheOnly: true);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(3, reports.Count);
        foreach (var driveLetter in "TUV")
        {
            AssertReport(ReportOf(reports, driveLetter), driveLetter, 3, BlockSource.WarmStartedFromCache,
                DriveState.Ready);
        }
    }

    [TestMethod]
    public async Task OpenAsync_CacheOnlyDeclinedDrive_ReportsItsBlocklessState()
    {
        var warmDrive = new IndexedDrive('T', CreateDriveRoot("first"), 1);
        var coldDrive = new IndexedDrive('U', CreateDriveRoot("second"), 2);

        await using (await FileIndex.OpenAsync(Options([warmDrive]), TestContext.CancellationTokenSource.Token))
        {
        }

        var reports = new ConcurrentQueue<IndexDriveOpened>();
        var options = Options([warmDrive, coldDrive], new SynchronousProgress<IndexDriveOpened>(reports.Enqueue),
            cacheOnly: true);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(2, reports.Count);
        AssertReport(ReportOf(reports, 'T'), 'T', 2, BlockSource.WarmStartedFromCache, DriveState.Ready);
        AssertReport(ReportOf(reports, 'U'), 'U', 2, BlockSource.None, DriveState.Failed);
        Assert.AreEqual(DriveState.Failed, index.Drives.Single(drive => drive.DriveLetter == 'U').State);
    }

    [TestMethod]
    public async Task OpenAsync_ColdScan_ReportsAfterTheProducerCompletes()
    {
        var reports = new ConcurrentQueue<IndexDriveOpened>();
        var reportCountAtProduceTime = -1;

        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            var result = new MftBlockProduceResult(MftBlockFixture.Build(request, journalId: 7, nextUsn: 4096, moment: MftBlockFixture.SeededMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0);
            reportCountAtProduceTime = reports.Count;
            return Task.FromResult(result);
        }

        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("first"), 1) };
        var options = Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Enqueue),
            producerPolicy: ProducerPolicy.Mft, mftProducer: FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, reportCountAtProduceTime, "the drive's report must follow its producer's completion");
        Assert.AreEqual(1, reports.Count);
        AssertReport(reports.Single(), 'T', 1, BlockSource.ProducedByScan, DriveState.Ready);
        Assert.AreEqual(1, reports.Single().SettledCount);
    }

    [TestMethod]
    public async Task OpenAsync_OfflineDrive_ReportsOfflineWithNoBlock()
    {
        var drives = new[] { new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1) };
        var reports = new ConcurrentQueue<IndexDriveOpened>();

        await using var index = await FileIndex.OpenAsync(
            Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Enqueue)),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, reports.Count);
        AssertReport(reports.Single(), 'Z', 1, BlockSource.None, DriveState.Offline);
        Assert.AreEqual(1, reports.Single().SettledCount);
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
        var reports = new ConcurrentQueue<IndexDriveOpened>();

        await using var index = await FileIndex.OpenAsync(
            Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Enqueue)),
            TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(1, reports.Count);

        await index.RescanAsync('T', TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, reports.Count, "a rescan has its own awaitable surface and stays silent on open progress");
    }

    [TestMethod]
    public async Task OpenAsync_InterleavedDrives_ReportsEachSettledStateOnce()
    {
        var warmDrive = new IndexedDrive('T', CreateDriveRoot("first"), 1);
        var coldDrive = new IndexedDrive('U', CreateDriveRoot("second"), 2);
        var offlineDrive = new IndexedDrive('V', Path.Combine(_treeRoot, "absent"), 3);

        // Pre-create cache for drive T
        await using (await FileIndex.OpenAsync(Options([warmDrive]), TestContext.CancellationTokenSource.Token))
        {
        }

        var reports = new ConcurrentQueue<IndexDriveOpened>();
        var options = Options([warmDrive, coldDrive, offlineDrive],
            new SynchronousProgress<IndexDriveOpened>(reports.Enqueue));
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(3, reports.Count);
        AssertReport(ReportOf(reports, 'T'), 'T', 3, BlockSource.WarmStartedFromCache, DriveState.Ready);
        AssertReport(ReportOf(reports, 'U'), 'U', 3, BlockSource.ProducedByScan, DriveState.Ready);
        AssertReport(ReportOf(reports, 'V'), 'V', 3, BlockSource.None, DriveState.Offline);

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
        var reports = new ConcurrentQueue<IndexDriveOpened>();

        async Task<MftBlockProduceResult> FailingProducer(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("simulated MFT producer failure");
        }

        var drives = new[] { new IndexedDrive('T', CreateDriveRoot("failing"), 1) };
        var options = Options(drives, new SynchronousProgress<IndexDriveOpened>(reports.Enqueue),
            producerPolicy: ProducerPolicy.Mft, mftProducer: FailingProducer);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, reports.Count);
        AssertReport(reports.Single(), 'T', 1, BlockSource.None, DriveState.Failed);
        Assert.AreEqual(DriveState.Failed, index.Drives[0].State);
        StringAssert.Contains(index.Drives[0].MftProducerFailureMessage, "simulated MFT producer failure");
    }

    [TestMethod]
    public async Task OpenAsync_ThrowingOpenProgressCallback_Throws()
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
