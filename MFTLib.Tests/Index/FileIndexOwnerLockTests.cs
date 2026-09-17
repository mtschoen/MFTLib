using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Two <see cref="FileIndex" /> instances over one cache directory: the second opener can
///     never validate, rename, sweep, or delete a block the first index owns. A cache-only
///     second open reports <see cref="DriveFailureKind.InUse" />; a non-cache-only second open
///     scans into a private block and leaves the canonical cache alone.
/// </summary>
[TestClass]
public class FileIndexOwnerLockTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    uint _volumeSerial;

    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
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

    FileIndexOptions Options(bool cacheOnly = false, IProgress<IndexScanProgress>? progress = null,
        Action<string>? diagnostics = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, _volumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            InitialOpenCacheOnly = cacheOnly,
            Progress = progress,
            Diagnostics = diagnostics
        };
    }

    string CanonicalPath => Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));

    [TestMethod]
    public async Task OpenAsync_CacheOnly_SecondIndexReportsInUseAndLeavesTheFirstIndexsBlockAlone()
    {
        await using (var first = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            Assert.IsTrue(File.Exists(CanonicalPath));

            await using (var second = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None))
            {
                var status = second.Drives.Single();
                Assert.AreEqual(DriveState.Failed, status.State);
                Assert.AreEqual(DriveFailureKind.InUse, status.FailureKind);
                Assert.AreEqual(
                    "Drive T: cache block is in use by another FileIndex and --cache-only forbids a scan.",
                    status.MftProducerFailureMessage);
                Assert.IsNull(status.DiscardedBlock);
                Assert.IsTrue(File.Exists(CanonicalPath),
                    "the second opener must not delete the first index's live block");
                Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);
            }

            Assert.IsTrue(File.Exists(CanonicalPath),
                "disposing the declined opener leaves the owner's block in place");
        }

        // The lock died with the first index, so a later open takes the slot and warm-starts.
        await using var third = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, third.Drives.Single().State);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, third.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task OpenAsync_NonCacheOnly_SecondIndexScansAPrivateBlockAndNeverTouchesTheCanonicalCache()
    {
        await using var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalBytes = await ReadAllBytesSharedAsync(CanonicalPath);

        string secondBlockPath;
        await using (var second = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            var status = second.Drives.Single();
            Assert.AreEqual(DriveState.Ready, status.State);
            Assert.AreEqual(BlockSource.ProducedByScan, status.BlockSource);
            Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
            secondBlockPath = second.CurrentSnapshot.GetDriveBlock(secondOrdinal).Block.Path;
            Assert.AreNotEqual(CanonicalPath, secondBlockPath);
            StringAssert.Contains(secondBlockPath, "mftlib-private-");
            Assert.IsTrue(File.Exists(secondBlockPath));
            CollectionAssert.AreEqual(canonicalBytes, await ReadAllBytesSharedAsync(CanonicalPath),
                "the owning index's canonical cache is not replaced, truncated, or deleted");
            Assert.IsNotNull(second.Find(Path.Combine(_treeRoot, "Documents", "readme.md")));
        }

        Assert.IsFalse(File.Exists(secondBlockPath),
            "the private block is delete-on-close, so disposing the second index removes it");
        Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);
    }

    [TestMethod]
    public async Task RescanAsync_SecondIndexDuringBlockedRescan_ReportsInUseAndTheRestoredCanonicalWarmStartsTheNextOpen()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new BlockOnFirstArmedReport();
        uint rowsBefore;
        DateTime timestampBefore;

        await using (var first = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None))
        {
            rowsBefore = first.Drives[0].RowCount;
            timestampBefore = first.Drives[0].ScanTimestamp;

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            progress.Armed = true;
            var rescan = first.RescanAsync('T', cancellationTokenSource.Token);
            await progress.Reported;

            // The rescan is blocked mid-scan with the canonical file renamed aside. The second
            // opener cannot take the owner lock, so it reports InUse instead of validating the
            // half-written replacement or sweeping the renamed-aside file.
            await using (var second = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None))
            {
                Assert.AreEqual(DriveState.Failed, second.Drives.Single().State);
                Assert.AreEqual(DriveFailureKind.InUse, second.Drives.Single().FailureKind);
            }

            cancellationTokenSource.Cancel();
            progress.Release();
            await AssertThrowsCancellation(() => rescan);

            Assert.IsTrue(File.Exists(CanonicalPath), "the canonical file must be restored");
            Assert.AreEqual(0, Directory.EnumerateFiles(_cacheDirectory,
                CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*").Count());
        }

        await using var third = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, third.Drives[0].State);
        Assert.AreEqual(rowsBefore, third.Drives[0].RowCount);
        Assert.AreEqual(timestampBefore, third.Drives[0].ScanTimestamp);
        Assert.IsNull(third.Drives[0].DiscardedBlock);
    }

    [TestMethod]
    public async Task RescanAsync_Completed_SecondIndexKeepsItsPrivateBlockWhenTheFirstIndexIsDisposed()
    {
        var deletions = new List<string>();
        var first = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add), CancellationToken.None);
        try
        {
            var oldSnapshot = first.CurrentSnapshot;

            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            await first.RescanAsync('T', CancellationToken.None);

            var retiredPaths = Directory
                .EnumerateFiles(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*")
                .ToList();
            Assert.AreEqual(1, retiredPaths.Count);

            string secondBlockPath;
            await using (var second = await FileIndex.OpenAsync(Options(), CancellationToken.None))
            {
                Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
                secondBlockPath = second.CurrentSnapshot.GetDriveBlock(secondOrdinal).Block.Path;
                StringAssert.Contains(secondBlockPath, "mftlib-private-");
                Assert.IsTrue(File.Exists(retiredPaths[0]),
                    "the second opener must not sweep the first index's retired sibling");

                await first.DisposeAsync();

                Assert.IsTrue(File.Exists(secondBlockPath),
                    "the first index's disposal deletes its own retired file, never the second index's block");
                Assert.IsFalse(File.Exists(retiredPaths[0]));
                Assert.AreEqual(DriveState.Ready, second.Drives.Single().State);
                Assert.IsNotNull(second.Find(Path.Combine(_treeRoot, "Documents", "readme.md")));
            }

            Assert.IsTrue(deletions.Any(entry => entry.Contains(".retired-") && entry.Contains("superseded")),
                "the owning index logged its retired file's delete at release");
            GC.KeepAlive(oldSnapshot);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RescanAsync_PrivateBlockWhileCanonicalIsOwned_RescansPrivatelyAndTakesTheCanonicalSlotOnceItIsFree()
    {
        var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        try
        {
            var canonicalBytes = await ReadAllBytesSharedAsync(CanonicalPath);

            await using var second = await FileIndex.OpenAsync(Options(), CancellationToken.None);
            Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
            var firstPrivatePath = second.CurrentSnapshot.GetDriveBlock(secondOrdinal).Block.Path;
            StringAssert.Contains(firstPrivatePath, "mftlib-private-");

            // While the first index owns the slot, the second index's rescan must not rename the
            // canonical file aside: it scans privately again.
            await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
            await second.RescanAsync('T', CancellationToken.None);

            var secondPrivatePath = second.CurrentSnapshot.GetDriveBlock(secondOrdinal).Block.Path;
            StringAssert.Contains(secondPrivatePath, "mftlib-private-");
            Assert.AreNotEqual(firstPrivatePath, secondPrivatePath);
            CollectionAssert.AreEqual(canonicalBytes, await ReadAllBytesSharedAsync(CanonicalPath),
                "the canonical cache still belongs to the first index, untouched by the rescan");
            Assert.AreEqual(0, Directory.EnumerateFiles(_cacheDirectory,
                CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*").Count());
            Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);

            // Once the owner is gone, the second index's next rescan takes the slot: the leftover
            // canonical file is renamed aside under the now-held lock and the new block takes the
            // canonical name, so later opens warm-start it.
            await first.DisposeAsync();

            await second.RescanAsync('T', CancellationToken.None);

            Assert.AreEqual(CanonicalPath, second.CurrentSnapshot.GetDriveBlock(secondOrdinal).Block.Path);
            Assert.AreEqual(DriveState.Ready, second.Drives.Single().State);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RescanAsync_InUseFailedDrive_ScansIntoAPrivateBlockWhileTheOwnerStaysLive()
    {
        await using var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalBytes = await ReadAllBytesSharedAsync(CanonicalPath);

        await using var second = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None);
        Assert.AreEqual(DriveFailureKind.InUse, second.Drives.Single().FailureKind);

        await second.RescanAsync('T', CancellationToken.None);

        var status = second.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);
        Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
        StringAssert.Contains(second.CurrentSnapshot.GetDriveBlock(secondOrdinal).Block.Path,
            "mftlib-private-");
        CollectionAssert.AreEqual(canonicalBytes, await ReadAllBytesSharedAsync(CanonicalPath));
    }

    /// <summary>
    ///     Blocks the scan thread at the first armed progress report until the test releases
    ///     it, so a second index can open against the cache mid-rescan at a deterministic
    ///     point: the canonical file is renamed aside and the replacement is still being
    ///     written. Starts disarmed so the initial open rides through unblocked.
    /// </summary>
    sealed class BlockOnFirstArmedReport : IProgress<IndexScanProgress>
    {
        readonly TaskCompletionSource _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Armed { get; set; }
        public Task Reported => _reported.Task;
        public void Release() => _release.TrySetResult();

        public void Report(IndexScanProgress value)
        {
            if (!Armed)
            {
                return;
            }

            _reported.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
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

    static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }
}
