using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What <see cref="FileIndex.Status" /> reports: online and offline states, an offline
///     drive's rescan contract, and <see cref="DriveBlockStatus.AccessDeniedSubtreeCount" />
///     reflecting the most recent scan.
/// </summary>
[TestClass]
public sealed class FileIndexDriveStatusTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    FileIndexOptions Options(IProgress<IndexScanProgress>? progress = null) =>
        Fixture.Options() with
        {
            ScanProgress = progress
        };

    [TestMethod]
    public async Task Status_ReportsOfflineWhenRootDirectoryIsAbsent()
    {
        var options = new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('Z'), Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration),
            CacheDirectory = CacheDirectoryPath
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(new VolumeKey('Z'), index.Status.Drive);
        Assert.AreEqual(DriveState.Offline, index.Status.State);
        Assert.IsNull(index.Status.Block);
        Assert.AreEqual(WatchState.NotStarted, index.Status.Watch.State);
        Assert.IsFalse(index.Status.Watch.IsSupported);
        Assert.IsNull(index.Status.Failure);
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
            Root = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('T'), TreeRoot, 4242, ProducerKind.Enumeration),
            CacheDirectory = CacheDirectoryPath
        }, TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(3u, status.Block!.LiveRowCount);
        Assert.IsLessThanOrEqualTo(index.HeaderOf().RowCount, status.Block.LiveRowCount);
        Assert.IsNull(status.Failure);
    }

    [TestMethod]
    public async Task RescanAsync_OfflineDrive_ReportsFailureBecauseItHasNoBlock()
    {
        var options = new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('Z'), Path.Combine(TreeRoot, "absent"), 1, ProducerKind.Enumeration),
            CacheDirectory = CacheDirectoryPath
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var exception = await DriveResultAssertions.FailureAsync<ArgumentException>(
            () => index.RescanAsync(CancellationToken.None));
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

        Assert.AreEqual(1, index.Status.Block!.AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Status.Block!.SkippedRecordCount);
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
            Assert.AreEqual(1, scanned.Status.Block!.AccessDeniedSubtreeCount);
        }

        await using var warm = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, warm.Status.Block!.Origin);
        Assert.AreEqual(1, warm.Status.Block!.AccessDeniedSubtreeCount);
    }

    [TestMethod]
    public async Task RescanAsync_ASubtreeVanishesMidScan_UpdatesTheAccessDeniedCountOnDriveStatus()
    {
        var documentsPath = Path.Combine(TreeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        var progress = new DeleteSubtreeOnReport(documentsPath, vanishingPath);

        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        Assert.AreEqual(0, index.Status.Block!.AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Status.Block!.SkippedRecordCount);

        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon", TestContext.CancellationToken);
        await index.RescanAsync(CancellationToken.None);

        Assert.AreEqual(1, index.Status.Block!.AccessDeniedSubtreeCount);
        Assert.AreEqual(0, index.Status.Block!.SkippedRecordCount);
    }

    /// <summary>
    ///     A read of <see cref="FileIndex.Status" /> that a disposal overtakes before the read
    ///     takes the index's state lock reports the disposal, not a drive the disposal already
    ///     unpublished.
    /// </summary>
    [TestMethod]
    public async Task Status_DisposalCompletesBeforeTheReadTakesTheStateLock_ThrowsObjectDisposed()
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

        Assert.ThrowsExactly<ObjectDisposedException>(() => index.Status);
        Assert.AreEqual(1, disposals);
    }
}
