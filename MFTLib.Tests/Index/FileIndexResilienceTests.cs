using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Failure, cancellation, and cleanup behavior for <see cref="FileIndex" />: a scan that
///     throws or is cancelled must not leak a mapping or a temp file, a rescan that fails must
///     leave the previous block usable, and superseded blocks (no-cache temp files and
///     cache-mode rescan leftovers) must be cleaned up deterministically.
/// </summary>
[TestClass]
public class FileIndexResilienceTests
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

    FileIndexOptions Options(bool noCache = false, IProgress<IndexScanProgress>? progress = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            NoCache = noCache,
            Progress = progress
        };
    }

    /// <summary>
    ///     Cancels the token the moment the first directory finishes, so cancellation lands
    ///     mid-scan rather than before anything was created. Starts disarmed so the same
    ///     instance can ride through an initial scan unharmed and only interrupt a later one
    ///     (a rescan reuses <see cref="FileIndexOptions.Progress" /> from the original open).
    /// </summary>
    sealed class CancelOnFirstReport(CancellationTokenSource cancellationTokenSource) : IProgress<IndexScanProgress>
    {
        public bool Armed { get; set; } = true;

        public void Report(IndexScanProgress value)
        {
            if (Armed)
            {
                cancellationTokenSource.Cancel();
            }
        }
    }

    static async Task AssertThrowsCancellation(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected an OperationCanceledException (or a derived type such as TaskCanceledException).");
    }

    [TestMethod]
    public async Task OpenAsync_CancelledMidScan_LeavesTheBlockUnlockedAndTheNextOpenColdScansCleanly()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var options = Options(progress: new CancelOnFirstReport(cancellationTokenSource));

        await AssertThrowsCancellation(() => FileIndex.OpenAsync(options, cancellationTokenSource.Token));

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        if (File.Exists(blockPath))
        {
            // Proves the partial block is not still mapped: File.Delete would succeed even on a
            // file BlockFile still has mapped, since BlockFile opens with FileShare.Delete. An
            // exclusive open with FileShare.None succeeds only if no handle - mapped or not -
            // remains open on the file at all.
            using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Drives[0].State);
        Assert.IsTrue(reopened.Drives[0].RowCount >= 3);
    }

    [TestMethod]
    public async Task OpenAsync_NoCacheMode_CancelledMidScan_DeletesTheTempBlockImmediately()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var options = Options(noCache: true, progress: new CancelOnFirstReport(cancellationTokenSource));

        await AssertThrowsCancellation(() => FileIndex.OpenAsync(options, cancellationTokenSource.Token));

        Assert.AreEqual(0, Directory.EnumerateFiles(Path.GetTempPath(),
            $"mftlib-nocache-*-{CacheDirectory.BlockFileName('T', 0x0BADF00D)}").Count());
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_LeavesThePreviousBlockUsable()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        var rowsBefore = index.Drives[0].RowCount;
        var timestampBefore = index.Drives[0].ScanTimestamp;

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        progress.Armed = true;

        await AssertThrowsCancellation(() => index.RescanAsync('T', cancellationTokenSource.Token));

        Assert.AreEqual(rowsBefore, index.Drives[0].RowCount);
        Assert.AreEqual(timestampBefore, index.Drives[0].ScanTimestamp);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_RestoresTheCanonicalFileSoTheNextOpenWarmStarts()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        uint rowsBefore;
        DateTime timestampBefore;

        await using (var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None))
        {
            rowsBefore = index.Drives[0].RowCount;
            timestampBefore = index.Drives[0].ScanTimestamp;

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            progress.Armed = true;

            await AssertThrowsCancellation(() => index.RescanAsync('T', cancellationTokenSource.Token));

            var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
            Assert.IsTrue(File.Exists(blockPath), "the canonical file must be restored, not left renamed aside");
            Assert.AreEqual(0, Directory.EnumerateFiles(_cacheDirectory,
                CacheDirectory.BlockFileName('T', 0x0BADF00D) + ".retired-*").Count());
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Drives[0].State);
        Assert.AreEqual(rowsBefore, reopened.Drives[0].RowCount);
        Assert.AreEqual(timestampBefore, reopened.Drives[0].ScanTimestamp);
        Assert.IsNull(reopened.Drives[0].DiscardedBlock);
    }

    [TestMethod]
    public async Task DisposeAsync_NoCacheMode_DeletesEveryRetiredTempBlock()
    {
        var index = await FileIndex.OpenAsync(Options(noCache: true), CancellationToken.None);
        string firstPath;
        string secondPath;
        try
        {
            Assert.IsTrue(index.TryGetDriveOrdinal('T', out var driveOrdinal));
            firstPath = index.CurrentSnapshot.GetDriveBlock(driveOrdinal).Block.Path;
            Assert.IsTrue(File.Exists(firstPath));

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            await index.RescanAsync('T', CancellationToken.None);
            secondPath = index.CurrentSnapshot.GetDriveBlock(driveOrdinal).Block.Path;

            Assert.AreNotEqual(firstPath, secondPath);
            Assert.IsTrue(File.Exists(firstPath), "the superseded temp block is still mapped until dispose");
        }
        finally
        {
            // Disposal is the action under test and the cleanup at once. A failed assertion above
            // must still reach it, or a mapped no-cache block is left directly under the shared
            // temp root, outside what [TestCleanup] removes, where it can fail an unrelated later
            // test's directory delete on Windows and hide the failure that actually mattered.
            await index.DisposeAsync();
        }

        Assert.IsFalse(File.Exists(firstPath));
        Assert.IsFalse(File.Exists(secondPath));
    }

    [TestMethod]
    public async Task RescanAsync_CacheMode_RetiredFileExistsWhileHeldAndIsGoneAfterReleaseNow()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.IsTrue(index.TryGetDriveOrdinal('T', out var driveOrdinal));
        var oldSnapshot = index.CurrentSnapshot;
        var canonicalPath = oldSnapshot.GetDriveBlock(driveOrdinal).Block.Path;

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);

        var newPath = index.CurrentSnapshot.GetDriveBlock(driveOrdinal).Block.Path;
        Assert.AreEqual(canonicalPath, newPath, "the new block takes the same canonical cache path");

        var retiredPaths = Directory
            .EnumerateFiles(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D) + ".retired-*")
            .ToList();
        Assert.AreEqual(1, retiredPaths.Count);
        Assert.IsTrue(File.Exists(retiredPaths[0]));

        oldSnapshot.ReleaseNow();

        Assert.IsFalse(File.Exists(retiredPaths[0]));
    }

    [TestMethod]
    public async Task RescanAsync_BackToBackRescans_ProduceDistinctRetiredNamesAndBothAreCleanedUpAfterRelease()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var firstSnapshot = index.CurrentSnapshot;

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);
        var secondSnapshot = index.CurrentSnapshot;

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "third.md"), "third");
        await index.RescanAsync('T', CancellationToken.None);

        var retiredPaths = Directory
            .EnumerateFiles(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D) + ".retired-*")
            .ToList();
        Assert.AreEqual(2, retiredPaths.Count, "each rescan should retire a distinctly named file");
        Assert.AreNotEqual(retiredPaths[0], retiredPaths[1]);
        Assert.IsTrue(retiredPaths.All(File.Exists));

        firstSnapshot.ReleaseNow();
        secondSnapshot.ReleaseNow();

        Assert.IsTrue(retiredPaths.All(path => !File.Exists(path)));
    }

    [TestMethod]
    public async Task ReadsAfterDispose_ThrowObjectDisposedException()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        await index.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(() => index.Drives);
        Assert.ThrowsException<ObjectDisposedException>(() => index.CurrentSnapshot);
        Assert.ThrowsException<ObjectDisposedException>(() => index.TryGetDriveOrdinal('T', out _));
        Assert.ThrowsException<ObjectDisposedException>(() => index.Scan(0));
    }

    [TestMethod]
    public async Task OpenAsync_InvalidBlockIsDiscarded_SetsDiscardedBlockOnTheStatus()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        var bytes = await File.ReadAllBytesAsync(blockPath);
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(blockPath, bytes);

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockValidationResult.WrongMagic, reopened.Drives[0].DiscardedBlock);
    }

    [TestMethod]
    public async Task OpenAsync_WarmStart_LeavesDiscardedBlockNull()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.IsNull(reopened.Drives[0].DiscardedBlock);
    }

    [TestMethod]
    public async Task RescanAsync_CanonicalCacheFileMissing_StillSucceedsWithoutRenamingAnything()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));

        // Simulates a canonical cache file removed out from under the index between opens, so
        // RenameAsideForRescan finds nothing to rename aside. Safe while the block is still
        // mapped: BlockFile opens with FileShare.Delete.
        File.Delete(canonicalPath);

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);

        Assert.IsTrue(File.Exists(canonicalPath));
        Assert.AreEqual(0, Directory.EnumerateFiles(_cacheDirectory,
            CacheDirectory.BlockFileName('T', 0x0BADF00D) + ".retired-*").Count());
    }

    [TestMethod]
    public async Task OpenAsync_CacheMode_DeletesAPreExistingRetiredSiblingForTheDrive()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var staleRetiredPath = Path.Combine(_cacheDirectory,
            CacheDirectory.BlockFileName('T', 0x0BADF00D) + ".retired-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(staleRetiredPath, "leftover from a killed process");

        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.IsFalse(File.Exists(staleRetiredPath));
    }

    [TestMethod]
    public async Task OpenAsync_NoCacheMode_DeletesAPreExistingStaleTempBlockForTheDrive()
    {
        var staleTempPath = Path.Combine(Path.GetTempPath(),
            $"mftlib-nocache-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName('T', 0x0BADF00D)}");
        await File.WriteAllTextAsync(staleTempPath, "leftover from a killed process");

        try
        {
            await using var index = await FileIndex.OpenAsync(Options(noCache: true), CancellationToken.None);

            Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
            Assert.IsFalse(File.Exists(staleTempPath));
        }
        finally
        {
            if (File.Exists(staleTempPath))
            {
                File.Delete(staleTempPath);
            }
        }
    }
}
