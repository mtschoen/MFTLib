using MFTLib.Index;
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

    FileIndexOptions Options(IProgress<IndexScanProgress>? progress = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
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
            CacheDirectory = _cacheDirectory
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(2, index.Drives.Count);
        Assert.AreEqual('Z', index.Drives[0].DriveLetter);
        Assert.AreEqual(DriveState.Offline, index.Drives[0].State);
        Assert.AreEqual('T', index.Drives[1].DriveLetter);
        Assert.AreEqual(DriveState.Ready, index.Drives[1].State);
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
            CacheDirectory = _cacheDirectory
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
    }

    [TestMethod]
    public async Task RescanAsync_ASubtreeVanishesMidScan_UpdatesTheAccessDeniedCountOnDriveStatus()
    {
        var documentsPath = Path.Combine(_treeRoot, "Documents");
        var vanishingPath = Path.Combine(documentsPath, "Vanishing");
        var progress = new DeleteSubtreeOnReport(documentsPath, vanishingPath);

        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        Assert.AreEqual(0, index.Drives[0].AccessDeniedSubtreeCount);

        Directory.CreateDirectory(vanishingPath);
        await File.WriteAllTextAsync(Path.Combine(vanishingPath, "inner.txt"), "gone soon");
        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(1, index.Drives[0].AccessDeniedSubtreeCount);
    }
}
