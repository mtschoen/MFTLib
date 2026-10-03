using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What <see cref="FileIndex.Drives" /> reports: configured order regardless of online or
///     offline state, an offline drive's rescan contract, and
///     <see cref="DriveStatus.AccessDeniedSubtreeCount" /> reflecting the most recent scan.
/// </summary>
[TestClass]
public class FileIndexDriveStatusTests
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
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(IProgress<IndexScanProgress>? progress = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            Progress = progress
        };
    }

    [TestMethod]
    public async Task Drives_FollowsTheConfiguredOrderRegardlessOfOnlineOrOffline()
    {
        var options = new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1),
                new IndexedDrive('T', _treeRoot, 0x0BADF00D)
            ],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(2, index.Drives.Count);
        Assert.AreEqual('Z', index.Drives[0].DriveLetter);
        Assert.AreEqual(DriveState.Offline, index.Drives[0].State);
        Assert.AreEqual(0u, index.Drives[0].LiveRowCount);
        Assert.AreEqual(DriveFailureKind.None, index.Drives[0].FailureKind);
        Assert.AreEqual('T', index.Drives[1].DriveLetter);
        Assert.AreEqual(DriveState.Ready, index.Drives[1].State);
    }

    [TestMethod]
    public async Task DriveStatus_ReportsLiveRowCountFromTheHeader()
    {
        Directory.Delete(Path.Combine(_treeRoot, "Documents"), recursive: true);
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "one.txt"), "1",
            TestContext.CancellationTokenSource.Token);
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "two.txt"), "2",
            TestContext.CancellationTokenSource.Token);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 4242)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, TestContext.CancellationTokenSource.Token);

        var status = index.Drives.Single();
        Assert.AreEqual(3u, status.LiveRowCount);
        Assert.IsTrue(status.LiveRowCount <= status.RowCount);
    }

    [TestMethod]
    public async Task RescanAsync_OfflineDrive_ThrowsBecauseItHasNoBlock()
    {
        var options = new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1),
                new IndexedDrive('T', _treeRoot, 0x0BADF00D)
            ],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var exception = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => index.RescanAsync('Z', CancellationToken.None));
        StringAssert.Contains(exception.Message, "has no block");
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
            // A rescan reuses the same FileIndexOptions.Progress instance as the original open,
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
        var documentsPath = Path.Combine(_treeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon");

        var options = Options(progress: new DeleteSubtreeOnReport(documentsPath, vanishingPath));
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, index.Drives[0].AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Drives[0].SkippedRecordCount);
    }

    [TestMethod]
    public async Task RescanAsync_ASubtreeVanishesMidScan_UpdatesTheAccessDeniedCountOnDriveStatus()
    {
        var documentsPath = Path.Combine(_treeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        var progress = new DeleteSubtreeOnReport(documentsPath, vanishingPath);

        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        Assert.AreEqual(0, index.Drives[0].AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Drives[0].SkippedRecordCount);

        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon");
        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(1, index.Drives[0].AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Drives[0].SkippedRecordCount);
    }

    [TestMethod]
    public async Task Drives_CallerListMutatedAfterOpen_LeavesTheIndexUnchanged()
    {
        var configured = new List<IndexedDrive> { new('T', _treeRoot, 0x0BADF00D) };
        var options = new FileIndexOptions
        {
            Drives = configured,
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };
        var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);
        try
        {
            var before = index.Drives.Select(drive => drive.DriveLetter).ToArray();

            configured.Add(new IndexedDrive('U', _treeRoot, 0x0BADF00E));

            CollectionAssert.AreEqual(before, index.Drives.Select(drive => drive.DriveLetter).ToArray());
            CollectionAssert.AreEqual(new[] { 'T' }, before);
        }
        finally
        {
            await index.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
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
            new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1),
            new IndexedDrive('T', _treeRoot, 0x0BADF00D));
        var options = new FileIndexOptions
        {
            Drives = configured,
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };

        var index = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token)
            .WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            var statuses = index.Drives;

            Assert.AreEqual(1, configured.EnumerationCount);
            CollectionAssert.AreEqual(new[] { 'Z', 'T' },
                statuses.Select(drive => drive.DriveLetter).ToArray());
            Assert.AreEqual(DriveState.Offline, statuses[0].State);
            Assert.AreEqual(DriveState.Ready, statuses[1].State);
        }
        finally
        {
            await index.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
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
        var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationTokenSource.Token)
            .WaitAsync(TimeSpan.FromSeconds(30));
        var disposals = 0;
        index.DrivesReadBeforeLockForTest = () =>
        {
            if (Interlocked.Increment(ref disposals) == 1)
            {
                index.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
        };

        Assert.ThrowsException<ObjectDisposedException>(() => index.Drives);
        Assert.AreEqual(1, disposals);
    }
}
