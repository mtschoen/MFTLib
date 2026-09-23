using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IReadOnlySet{char}, System.Action{string})" /> exercised against a real, live
///     <see cref="FileIndex" /> owner rather than hand-written block files: MFTLib issue 234
///     requires that a clear never deletes, tears, or unlinks a block another index actually
///     owns, whether that ownership is settled before the clear runs, still in flight, or racing
///     a concurrent open. This class also installs <see cref="CacheDirectory._beforeDeleteForTest" />,
///     a process-wide test seam with no synchronization of its own, so it must not run
///     concurrently with any other test that calls <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IReadOnlySet{char}, System.Action{string})" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CacheDirectoryDeletionLiveOwnerTests
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

    FileIndexOptions Options(bool cacheOnly = false, IProgress<IndexScanProgress>? progress = null)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, _volumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            InitialOpenCacheOnly = cacheOnly,
            Progress = progress
        };
    }

    string CanonicalPath => Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));

    static string CreateDecoyBlock(string cacheDirectory, char letter)
    {
        var path = Path.Combine(cacheDirectory, CacheDirectory.BlockFileName(letter, 1));
        File.WriteAllText(path, "deliberately invalid decoy block content, never opened by a real index");
        return path;
    }

    static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    [TestMethod]
    public async Task DeleteCached_ClearingWhileALiveFileIndexOwnsABlock_DeletesUnownedBlocksReportsTheOwnedOneInUseAndPreservesEveryLockFile()
    {
        await using var owner = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.IsTrue(File.Exists(CanonicalPath), "the live index must have produced its canonical block");

        var unownedFirst = CreateDecoyBlock(_cacheDirectory, 'U');
        var unownedSecond = CreateDecoyBlock(_cacheDirectory, 'V');
        var messages = new List<string>();

        var results = CacheDirectory.DeleteCached(_cacheDirectory, diagnostics: messages.Add);

        Assert.AreEqual(3, results.Count);
        var ownedResult = results.Single(result => result.File.Path == CanonicalPath);
        Assert.AreEqual(CachedBlockDeletionOutcome.InUse, ownedResult.Outcome);
        Assert.IsNull(ownedResult.FailureReason);
        foreach (var unowned in new[] { unownedFirst, unownedSecond })
        {
            var result = results.Single(candidate => candidate.File.Path == unowned);
            Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, result.Outcome);
            Assert.IsFalse(File.Exists(unowned), "an unowned decoy block must be deleted");
        }

        Assert.IsTrue(File.Exists(CanonicalPath), "the live owner's block must survive the clear");
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(CanonicalPath)),
            "the owner's own lock file must never be deleted");
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(unownedFirst)),
            "a lock file created while deleting an unowned block must be preserved");
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(unownedSecond)),
            "a lock file created while deleting an unowned block must be preserved");
        Assert.IsFalse(messages.Contains($"Deleted block file '{CanonicalPath}': clearing a cached block."),
            "the owned block was never deleted, so it must never be logged as deleted");
        Assert.AreEqual(2, messages.Count);

        Assert.AreEqual(DriveState.Ready, owner.Drives.Single().State);
        Assert.IsNotNull(owner.Find(Path.Combine(_treeRoot, "Documents", "readme.md")),
            "the live index keeps working after unrelated blocks are cleared out from under it");
    }

    /// <summary>
    ///     Parks the first armed progress report until the test releases it, so
    ///     <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IReadOnlySet{char}, System.Action{string})" /> can run concurrently at a deterministic point
    ///     mid-rescan: the canonical file already holds the half-written replacement and the owner
    ///     lock is still held. Starts disarmed so the initial open rides through unblocked.
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

    [TestMethod]
    public async Task DeleteCached_RacingAConcurrentRescan_ReportsInUseAndNeverDeletesTheBlockBeingWritten()
    {
        var progress = new BlockOnFirstArmedReport();
        await using var owner = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        var canonicalBytesBeforeRescan = await ReadAllBytesSharedAsync(CanonicalPath);

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        progress.Armed = true;

        // Parks mid-rescan: the canonical file has already been renamed aside and the
        // replacement is being written back to the canonical path while the owner lock is held.
        var rescan = owner.RescanAsync('T', CancellationToken.None);
        await progress.Reported;

        try
        {
            var results = CacheDirectory.DeleteCached(_cacheDirectory);

            var result = results.Single();
            Assert.AreEqual(CachedBlockDeletionOutcome.InUse, result.Outcome);
            Assert.IsNull(result.FailureReason);
            Assert.IsTrue(File.Exists(CanonicalPath),
                "a clear racing an in-flight rescan must never delete the block being written");
        }
        finally
        {
            progress.Release();
            await rescan;
        }

        Assert.AreEqual(DriveState.Ready, owner.Drives.Single().State);
        var canonicalBytesAfterRescan = await ReadAllBytesSharedAsync(CanonicalPath);
        CollectionAssert.AreNotEqual(canonicalBytesBeforeRescan, canonicalBytesAfterRescan,
            "the rescan completed normally once the clear backed off from the held lock");
    }

    /// <summary>
    ///     Parks the first call to <see cref="CacheDirectory._beforeDeleteForTest" /> until the
    ///     test releases it, so <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IReadOnlySet{char}, System.Action{string})" /> can be driven from a
    ///     background task while it still holds the candidate block's owner lock and the file has
    ///     not been deleted yet, at a deterministic point a concurrent <see cref="FileIndex.OpenAsync" />
    ///     can race against.
    /// </summary>
    sealed class DeleteParkGate
    {
        readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Parked => _parked.Task;
        public void Release() => _release.TrySetResult();

        public void ParkOnce(string path)
        {
            _parked.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public async Task DeleteCached_RacingAConcurrentOpen_NeverAdoptsOrCorruptsTheSlotAndAFreshOpenSucceedsAfterwards()
    {
        await using (var seed = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            Assert.IsTrue(seed.Find(Path.Combine(_treeRoot, "Documents", "readme.md")) is not null);
        }
        var canonicalBytesBeforeRace = await ReadAllBytesSharedAsync(CanonicalPath);

        var gate = new DeleteParkGate();
        Task<IReadOnlyList<CachedBlockDeletionResult>> deleteTask;
        using (CacheDirectory.ParkBeforeDeleteForTest(gate.ParkOnce))
        {
            // Runs DeleteCached on a background thread so it can park mid-delete, still holding
            // the candidate block's owner lock, while this thread races opens against it.
            deleteTask = Task.Run(() => CacheDirectory.DeleteCached(_cacheDirectory));
            await gate.Parked;

            await using (var cacheOnlyOpen = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None))
            {
                var status = cacheOnlyOpen.Drives.Single();
                Assert.AreEqual(DriveState.Failed, status.State);
                Assert.AreEqual(DriveFailureKind.InUse, status.FailureKind);
                Assert.IsTrue(File.Exists(CanonicalPath),
                    "a cache-only open racing a parked delete must not delete or adopt the slot");
                CollectionAssert.AreEqual(canonicalBytesBeforeRace, await ReadAllBytesSharedAsync(CanonicalPath),
                    "the slot must be byte-identical while the delete still holds it");
            }

            string privateBlockPath;
            await using (var fallbackOpen = await FileIndex.OpenAsync(Options(), CancellationToken.None))
            {
                var status = fallbackOpen.Drives.Single();
                Assert.AreEqual(DriveState.Ready, status.State);
                Assert.AreEqual(BlockSource.ProducedByScan, status.BlockSource);
                Assert.IsTrue(fallbackOpen.TryGetDriveOrdinal('T', out var ordinal));
                privateBlockPath = fallbackOpen.CurrentSnapshot.GetDriveBlock(ordinal).Block.Path;
                StringAssert.Contains(privateBlockPath, "mftlib-private-");
                Assert.AreNotEqual(CanonicalPath, privateBlockPath);
                Assert.IsNotNull(fallbackOpen.Find(Path.Combine(_treeRoot, "Documents", "readme.md")));
                CollectionAssert.AreEqual(canonicalBytesBeforeRace, await ReadAllBytesSharedAsync(CanonicalPath),
                    "a non-cache-only open racing a parked delete must fall back to a private block "
                    + "instead of touching the slot the delete still holds");
            }
            Assert.IsFalse(File.Exists(privateBlockPath), "the fallback's private block is delete-on-close");

            gate.Release();
            var results = await deleteTask;
            var result = results.Single();
            Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, result.Outcome);
        }

        Assert.IsFalse(File.Exists(CanonicalPath), "the clear completes once it releases the lock");
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(CanonicalPath)), "the lock file itself is never deleted");

        await using var freshOpen = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var freshStatus = freshOpen.Drives.Single();
        Assert.AreEqual(DriveState.Ready, freshStatus.State);
        Assert.AreEqual(BlockSource.ProducedByScan, freshStatus.BlockSource);
        Assert.IsTrue(freshOpen.TryGetDriveOrdinal('T', out var freshOrdinal));
        Assert.AreEqual(CanonicalPath, freshOpen.CurrentSnapshot.GetDriveBlock(freshOrdinal).Block.Path,
            "once the clear has released the lock, a fresh open takes the slot normally");
    }
}
