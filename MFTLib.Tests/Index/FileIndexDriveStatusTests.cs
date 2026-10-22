using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What <see cref="FileIndex.Drives" /> reports: configured order regardless of online or
///     offline state, an offline drive's rescan contract, and
///     <see cref="DriveBlockStatus.AccessDeniedSubtreeCount" /> reflecting the most recent scan.
/// </summary>
[TestClass]
public sealed class FileIndexDriveStatusTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;
    string CacheDirectoryPath => _fixture.CacheDirectory;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture();

    public void Dispose() => _fixture.Dispose();

    FileIndexOptions Options(IProgress<IndexScanProgress>? progress = null) =>
        _fixture.Options() with
        {
            ScanProgress = progress
        };

    [TestMethod]
    public async Task Drives_FollowsTheConfiguredOrderRegardlessOfOnlineOrOffline()
    {
        var options = new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('Z', Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration),
                new IndexedDrive('T', TreeRoot, 0x0BADF00D, ProducerKind.Enumeration)
            ],
            CacheDirectory = CacheDirectoryPath
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.HasCount(2, index.Drives);
        Assert.AreEqual('Z', index.Drives[0].Drive);
        Assert.AreEqual(DriveState.Offline, index.Drives[0].State);
        Assert.IsNull(index.Drives[0].Block);
        Assert.AreEqual(WatchState.NotStarted, index.Drives[0].Watch.State);
        Assert.IsFalse(index.Drives[0].Watch.IsSupported);
        Assert.IsNull(index.Drives[0].Failure);
        Assert.AreEqual('T', index.Drives[1].Drive);
        Assert.AreEqual(DriveState.Ready, index.Drives[1].State);
    }

    [TestMethod]
    public async Task DriveStatus_ReportsLiveRowCountFromTheHeader()
    {
        Directory.Delete(Path.Combine(TreeRoot, "Documents"), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "one.txt"), "1",
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "two.txt"), "2",
            TestContext.CancellationToken);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', TreeRoot, 4242, ProducerKind.Enumeration)],
            CacheDirectory = CacheDirectoryPath
        }, TestContext.CancellationToken);

        var status = index.Drives.Single();
        Assert.AreEqual(3u, status.Block!.LiveRowCount);
        Assert.IsLessThanOrEqualTo(index.HeaderOf().RowCount, status.Block.LiveRowCount);
        Assert.IsNull(status.Failure);
    }

    [TestMethod]
    public async Task RescanAsync_OfflineDrive_ReportsFailureBecauseItHasNoBlock()
    {
        var options = new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('Z', Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration),
                new IndexedDrive('T', TreeRoot, 0x0BADF00D, ProducerKind.Enumeration)
            ],
            CacheDirectory = CacheDirectoryPath
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var exception = await DriveResultAssertions.FailureAsync<ArgumentException>(
            () => index.RescanAsync(['Z'], CancellationToken.None));
        Assert.Contains("has no block", exception.Message);
    }

    /// <summary>
    ///     Deletes <paramref name="directoryToDelete" /> the moment progress reports the walk
    ///     just finished <paramref name="triggerDirectory" />, which is exactly the point between
    ///     that directory being enumerated (queuing its child for a later pass) and the child
    ///     actually being dequeued and opened. Deterministic and admin-free: it provokes a real
    ///     DirectoryNotFoundException from the production access-denied handling path without
    ///     touching an ACL.
    /// </summary>
    sealed class DeleteSubtreeOnReport(string triggerDirectory, string directoryToDelete)
        : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value)
        {
            // A rescan reuses the same FileIndexOptions.ScanProgress instance as the original open,
            // so this must be a no-op on any pass where the directory is not there yet to delete.
            if (value.CurrentDirectory == triggerDirectory && Directory.Exists(directoryToDelete))
            {
                Directory.Delete(directoryToDelete, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OpenAsync_ASubtreeVanishesMidScan_CountsItAndSurfacesOnDriveStatus()
    {
        var documentsPath = Path.Combine(TreeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon", TestContext.CancellationToken);

        var options = Options(progress: new DeleteSubtreeOnReport(documentsPath, vanishingPath));
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, index.Drives[0].Block!.AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Drives[0].Block!.SkippedRecordCount);
    }

    [TestMethod]
    public async Task OpenAsync_WarmStart_ReportsTheAccessDeniedCountOfTheScanThatBuiltTheBlock()
    {
        var documentsPath = Path.Combine(TreeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon", TestContext.CancellationToken);
        await using (var scanned = await FileIndex.OpenAsync(
            Options(progress: new DeleteSubtreeOnReport(documentsPath, vanishingPath)), CancellationToken.None))
        {
            Assert.AreEqual(1, scanned.Drives[0].Block!.AccessDeniedSubtreeCount);
        }

        await using var warm = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, warm.Drives[0].Block!.Origin);
        Assert.AreEqual(1, warm.Drives[0].Block!.AccessDeniedSubtreeCount);
    }

    [TestMethod]
    public async Task RescanAsync_ASubtreeVanishesMidScan_UpdatesTheAccessDeniedCountOnDriveStatus()
    {
        var documentsPath = Path.Combine(TreeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        var progress = new DeleteSubtreeOnReport(documentsPath, vanishingPath);

        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        Assert.AreEqual(0, index.Drives[0].Block!.AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Drives[0].Block!.SkippedRecordCount);

        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);

        Assert.AreEqual(1, index.Drives[0].Block!.AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Drives[0].Block!.SkippedRecordCount);
    }

    [TestMethod]
    public async Task Drives_CallerListMutatedAfterOpen_LeavesTheIndexUnchanged()
    {
        var configured = new List<IndexedDrive> { new('T', TreeRoot, 0x0BADF00D, ProducerKind.Enumeration) };
        var options = new FileIndexOptions
        {
            Drives = configured,
            CacheDirectory = CacheDirectoryPath
        };
        var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        try
        {
            var before = index.Drives.Select(drive => drive.Drive).ToArray();

            configured.Add(new IndexedDrive('U', TreeRoot, 0x0BADF00E));

            Assert.AreSequenceEqual(before, [.. index.Drives.Select(drive => drive.Drive)]);
            Assert.AreSequenceEqual(['T'], before);
        }
        finally
        {
            await index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        }
    }

    /// <summary>
    ///     A caller-owned drive list that can be enumerated exactly once; any later enumeration
    ///     throws, so an index that reads the caller's list after taking its private copy fails.
    /// </summary>
    sealed class EnumerableOnceDriveList(params IndexedDrive[] drives) : IReadOnlyList<IndexedDrive>
    {
        int _enumerationCount;

        public int EnumerationCount => _enumerationCount;

        public int Count => drives.Length;

        public IndexedDrive this[int index] => drives[index];

        public IEnumerator<IndexedDrive> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) > 1)
            {
                throw new InvalidOperationException("The caller's drive list was enumerated a second time.");
            }

            return ((IEnumerable<IndexedDrive>)drives).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public async Task OpenAsync_CallerDriveListEnumerableOnlyOnce_OpensFromTheFirstEnumeration()
    {
        var configured = new EnumerableOnceDriveList(
            new IndexedDrive('Z', Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration),
            new IndexedDrive('T', TreeRoot, 0x0BADF00D, ProducerKind.Enumeration));
        var options = new FileIndexOptions
        {
            Drives = configured,
            CacheDirectory = CacheDirectoryPath
        };

        var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken)
            .WaitAsync(HangGuard * 3, TestContext.CancellationToken);
        try
        {
            var statuses = index.Drives;

            Assert.AreEqual(1, configured.EnumerationCount);
            Assert.AreSequenceEqual(['Z', 'T'], [.. statuses.Select(drive => drive.Drive.Letter)]);
            Assert.AreEqual(DriveState.Offline, statuses[0].State);
            Assert.AreEqual(DriveState.Ready, statuses[1].State);
        }
        finally
        {
            await index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        }
    }

    /// <summary>
    ///     A read of <see cref="FileIndex.Drives" /> that a disposal overtakes before the read
    ///     takes the index's state lock reports the disposal, not a drive the disposal already
    ///     unpublished.
    /// </summary>
    [TestMethod]
    public async Task Drives_DisposalCompletesBeforeTheReadTakesTheStateLock_ThrowsObjectDisposed()
    {
        var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationToken)
            .WaitAsync(HangGuard * 3, TestContext.CancellationToken);
        var disposals = 0;
        index.Hooks.DrivesReadBeforeLock = () =>
        {
            if (Interlocked.Increment(ref disposals) == 1)
            {
                index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken).GetAwaiter().GetResult();
            }
        };

        Assert.ThrowsExactly<ObjectDisposedException>(() => index.Drives);
        Assert.AreEqual(1, disposals);
    }
}
