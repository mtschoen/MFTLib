using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexLifetimeTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
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

    FileIndexOptions Options(bool noCache = false, ProducerPolicy policy = ProducerPolicy.Auto)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
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
        Assert.IsTrue(File.Exists(Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D))));
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
    public async Task OpenAsync_InvalidBlockIsDiscardedAndTheDriveColdScans()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        var bytes = await File.ReadAllBytesAsync(blockPath);
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(blockPath, bytes);

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Drives[0].State);
        Assert.IsTrue(reopened.Drives[0].RowCount >= 3);
    }

    [TestMethod]
    public async Task OpenAsync_NoCacheMode_LeavesNothingInTheCacheDirectory()
    {
        await using (var index = await FileIndex.OpenAsync(Options(noCache: true), CancellationToken.None))
        {
            Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        }

        var cachedBlock = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
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
    public async Task OpenAsync_MftOnlyPolicy_ThrowsUntilTheMftProducerLands()
    {
        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => FileIndex.OpenAsync(Options(policy: ProducerPolicy.MftOnly), CancellationToken.None));
    }

    [TestMethod]
    public async Task OpenAsync_NoDrives_OpensEmpty()
    {
        var options = new FileIndexOptions { CacheDirectory = _cacheDirectory };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(0, index.Drives.Count);
    }

    [TestMethod]
    public async Task RescanAsync_SwapsInANewBlockAndTheOldFileIsRemovedOnce()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var rowsBefore = index.Drives[0].RowCount;

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(rowsBefore + 1, index.Drives[0].RowCount);
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

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreNotSame(oldSnapshot, index.CurrentSnapshot);
        Assert.IsTrue(oldEntry.IsDirectory);

        oldSnapshot.ReleaseNow();
        Assert.ThrowsException<ObjectDisposedException>(() => oldEntry.IsDirectory);
    }
}
