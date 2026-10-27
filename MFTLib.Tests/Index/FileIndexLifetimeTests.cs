using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexLifetimeTests : IndexTreeTestBase
{
    FileIndexOptions Options(bool cacheDisabled = false, ProducerKind producerKind = ProducerKind.Enumeration) =>
        Fixture.Options(volumeSerial: UniqueVolumeSerial, producer: producerKind,
            cacheMode: cacheDisabled ? CacheMode.Disabled : CacheMode.Persistent);

    [TestMethod]
    public async Task OpenAsync_ColdScansAndWritesABlockIntoTheCacheDirectory()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.HasCount(1, index.Drives);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
        Assert.IsGreaterThanOrEqualTo(3u, index.HeaderOf().RowCount);
        Assert.IsFalse(index.Drives[0].Watch.IsSupported);
        Assert.IsTrue(File.Exists(Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', UniqueVolumeSerial))));
    }

    [TestMethod]
    public async Task OpenAsync_SecondOpenWarmStartsFromTheExistingBlock()
    {
        DateTime firstTimestamp;
        await using (var first = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            firstTimestamp = first.Drives[0].Block!.ScanTimestamp;
        }

        await using var second = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(firstTimestamp, second.Drives[0].Block!.ScanTimestamp);
    }


    [TestMethod]
    public async Task OpenAsync_DisabledCacheMode_LeavesNothingInTheCacheDirectory()
    {
        await using (var index = await FileIndex.OpenAsync(Options(cacheDisabled: true), CancellationToken.None))
        {
            Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        }


        var cachedBlock = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', UniqueVolumeSerial));
        Assert.IsFalse(File.Exists(cachedBlock));
    }

    [TestMethod]
    public async Task OpenAsync_MissingRootDirectory_ReportsTheDriveOffline()
    {
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('Z', Path.Combine(TreeRoot, "absent"), 1)],
            CacheDirectory = CacheDirectoryPath
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Offline, index.Drives[0].State);
        Assert.IsNull(index.Drives[0].Block);
        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, 'Z'));
    }


    [TestMethod]
    public async Task OpenAsync_NoDrives_OpensEmpty()
    {
        var options = new FileIndexOptions { CacheDirectory = CacheDirectoryPath };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.IsEmpty(index.Drives);
    }

    [TestMethod]
    public async Task RescanAsync_UnknownDrive_Throws()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => index.RescanAsync(['Q'], CancellationToken.None));
    }

    [TestMethod]
    public async Task DisposeAsync_IsIdempotent()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await index.DisposeAsync();
        await index.DisposeAsync();
    }

    /// <summary>
    ///     A rescan writes its replacement block to the same canonical path as the block it
    ///     supersedes. This proves a handle minted from the pre-rescan snapshot still reads
    ///     valid data afterward (the old mapping survives because BlockFile opens with
    ///     FileShare.Delete, so deleting the canonical path to make room for the new file does
    ///     not disturb the still-open old handle), and that the handle stops working once its
    ///     snapshot is actually released, not merely superseded.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_AHeldHandleFromTheOldSnapshotStaysValidUntilItIsReleased()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var oldSnapshot = index.CurrentSnapshot;
        Assert.IsTrue(index.TryGetDriveOrdinal('T', out var driveOrdinal));
        var oldEntry = FileEntry.Create(oldSnapshot[driveOrdinal], rowIndex: 0);
        var rowsBefore = index.HeaderOf().RowCount;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);

        Assert.AreNotSame(oldSnapshot, index.CurrentSnapshot);
        Assert.IsTrue(oldEntry.IsDirectory);
        Assert.AreEqual(rowsBefore + 1, index.HeaderOf().RowCount);

        await oldSnapshot.ReleaseNowAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => oldEntry.IsDirectory);
    }

    /// <summary>
    ///     MFTLib#145 reversed this: a handle held across disposal used to stay readable because
    ///     disposal skipped the release whenever a query had handed out a handle on it. Disposal
    ///     now always unmaps, so the handle answers <see cref="ObjectDisposedException" /> instead
    ///     of keeping the block file open for a garbage collection that may never come.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_AHeldFileEntryBecomesDisposedAndThrows()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken).Single();
        Assert.AreEqual("readme.md", entry.Name);

        await index.DisposeAsync();

        Assert.IsTrue(entry.IsValid);
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = entry.Name);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = entry.RecordKey);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = entry.Path);
    }

    public TestContext TestContext { get; set; } = null!;
}
