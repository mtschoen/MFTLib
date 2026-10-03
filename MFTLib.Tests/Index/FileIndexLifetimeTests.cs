using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexLifetimeTests
{
    OwnedIndexDirectories _directories = null!;
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    uint _volumeSerial;

    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
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

    FileIndexOptions Options(bool noCache = false, ProducerPolicy policy = ProducerPolicy.Enumeration)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, _volumeSerial)],
            CacheDirectory = _cacheDirectory,
            NoCache = noCache,
            ProducerPolicy = policy
        };
    }

    [TestMethod]
    public async Task OpenAsync_ColdScansAndWritesABlockIntoTheCacheDirectory()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(1, index.Drives.Count);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(ProducerKind.Enumeration, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].RowCount >= 3);
        Assert.IsFalse(index.Drives[0].WatchSupported);
        Assert.IsTrue(File.Exists(Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial))));
    }

    [TestMethod]
    public async Task OpenAsync_SecondOpenWarmStartsFromTheExistingBlock()
    {
        DateTime firstTimestamp;
        await using (var first = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            firstTimestamp = first.Drives[0].ScanTimestamp;
        }

        await using var second = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(firstTimestamp, second.Drives[0].ScanTimestamp);
    }


    [TestMethod]
    public async Task OpenAsync_NoCacheMode_LeavesNothingInTheCacheDirectory()
    {
        await using (var index = await FileIndex.OpenAsync(Options(noCache: true), CancellationToken.None))
        {
            Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        }


        var cachedBlock = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        Assert.IsFalse(File.Exists(cachedBlock));
    }

    [TestMethod]
    public async Task OpenAsync_MissingRootDirectory_ReportsTheDriveOffline()
    {
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1)],
            CacheDirectory = _cacheDirectory
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Offline, index.Drives[0].State);
        Assert.AreEqual(0u, index.Drives[0].RowCount);
    }


    [TestMethod]
    public async Task OpenAsync_NoDrives_OpensEmpty()
    {
        var options = new FileIndexOptions { CacheDirectory = _cacheDirectory };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(0, index.Drives.Count);
    }

    [TestMethod]
    public async Task RescanAsync_UnknownDrive_Throws()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => index.RescanAsync('Q', CancellationToken.None));
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
        var oldEntry = FileEntry.Create(oldSnapshot, driveOrdinal, rowIndex: 0);
        var rowsBefore = index.Drives[driveOrdinal].RowCount;

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreNotSame(oldSnapshot, index.CurrentSnapshot);
        Assert.IsTrue(oldEntry.IsDirectory);
        Assert.AreEqual(rowsBefore + 1, index.Drives[driveOrdinal].RowCount);

        await oldSnapshot.ReleaseNowAsync();
        Assert.ThrowsException<ObjectDisposedException>(() => oldEntry.IsDirectory);
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
        var entry = index.FindByName("readme.md").Single();
        Assert.AreEqual("readme.md", entry.Name);

        await index.DisposeAsync();

        Assert.IsTrue(entry.IsValid);
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Name);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Id);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Path);
    }
}
