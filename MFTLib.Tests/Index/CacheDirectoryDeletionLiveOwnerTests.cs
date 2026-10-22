using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IEnumerable{DriveKey})" /> exercised against a real, live
///     <see cref="FileIndex" /> owner rather than hand-written block files: MFTLib issue 234
///     requires that a clear never deletes, tears, or unlinks a block another index actually
///     owns, whether that ownership is settled before the clear runs, still in flight, or racing
///     a concurrent open. This class also installs <see cref="CacheDirectory.Hooks" />,
///     a process-wide test seam with no synchronization of its own, so it must not run
///     concurrently with any other test that calls <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IEnumerable{DriveKey})" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CacheDirectoryDeletionLiveOwnerTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;
    string CacheDirectoryPath => _fixture.CacheDirectory;
    uint _volumeSerial;

    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
        _fixture = new IndexTreeFixture();
    }

    public void Dispose()
    {
        CacheDirectory.Hooks.ResetToDefaults();
        _fixture.Dispose();
    }

    FileIndexOptions Options(bool cacheOnly = false, IProgress<IndexScanProgress>? progress = null) =>
        _fixture.Options(volumeSerial: _volumeSerial,
            cacheMode: cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent) with
        {
            ScanProgress = progress
        };

    string CanonicalPath => Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));

    static string CreateDecoyBlock(string cacheDirectory, char letter)
    {
        var path = Path.Combine(cacheDirectory, CacheDirectory.BlockFileName(letter, 1));
        File.WriteAllText(path, "deliberately invalid decoy block content, never opened by a real index");
        return path;
    }


    [TestMethod]
    public async Task DeleteCached_ClearingWhileALiveFileIndexOwnsABlock_DeletesUnownedBlocksReportsTheOwnedOneInUseAndPreservesEveryLockFile()
    {
        await using var owner = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.IsTrue(File.Exists(CanonicalPath), "the live index must have produced its canonical block");

        var unownedFirst = CreateDecoyBlock(CacheDirectoryPath, 'U');
        var unownedSecond = CreateDecoyBlock(CacheDirectoryPath, 'V');

        var results = CacheDirectory.DeleteCached(CacheDirectoryPath);

        Assert.HasCount(3, results);
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

        Assert.AreEqual(DriveState.Ready, owner.Drives.Single().State);
        Assert.IsNotNull(owner.Find(Path.Combine(TreeRoot, "Documents", "readme.md")),
            "the live index keeps working after unrelated blocks are cleared out from under it");
    }

    [TestMethod]
    public async Task DeleteCached_RacingAConcurrentRescan_ReportsInUseAndNeverDeletesTheBlockBeingWritten()
    {
        var progress = new BlockOnFirstArmedReport();
        await using var owner = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        var canonicalBytesBeforeRescan = await SharedFileReader.ReadAllBytesAsync(CanonicalPath);

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second");
        progress.Armed = true;

        // Parks mid-rescan: the canonical file has already been renamed aside and the
        // replacement is being written back to the canonical path while the owner lock is held.
        var rescan = owner.RescanAsync(['T'], CancellationToken.None);
        await progress.Reported;

        try
        {
            var results = CacheDirectory.DeleteCached(CacheDirectoryPath);

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
        var canonicalBytesAfterRescan = await SharedFileReader.ReadAllBytesAsync(CanonicalPath);
        Assert.AreNotSequenceEqual(canonicalBytesBeforeRescan, canonicalBytesAfterRescan,
            "the rescan completed normally once the clear backed off from the held lock");
    }

    /// <summary>
    ///     Parks the first call to <see cref="CacheDirectory.Hooks" /> until the
    ///     test releases it, so <see cref="CacheDirectory.DeleteCached(string, System.Collections.Generic.IEnumerable{DriveKey})" /> can be driven from a
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
            Assert.IsNotNull(seed.Find(Path.Combine(TreeRoot, "Documents", "readme.md")));
        }
        var canonicalBytesBeforeRace = await SharedFileReader.ReadAllBytesAsync(CanonicalPath);

        var gate = new DeleteParkGate();
        CacheDirectory.Hooks._beforeDelete = gate.ParkOnce;
        // Runs DeleteCached on a background thread so it can park mid-delete, still holding
        // the candidate block's owner lock, while this thread races opens against it.
        var deleteTask = Task.Run(() => CacheDirectory.DeleteCached(CacheDirectoryPath));
        await gate.Parked;

        await using (var cacheOnlyOpen = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None))
        {
            var status = cacheOnlyOpen.Drives.Single();
            Assert.AreEqual(DriveState.Failed, status.State);
            Assert.AreEqual(DriveFailureKind.InUse, status.Failure?.Kind);
            Assert.IsTrue(File.Exists(CanonicalPath),
                "a cache-only open racing a parked delete must not delete or adopt the slot");
            Assert.AreSequenceEqual(canonicalBytesBeforeRace, await SharedFileReader.ReadAllBytesAsync(CanonicalPath), "the slot must be byte-identical while the delete still holds it");
        }

        string privateBlockPath;
        await using (var fallbackOpen = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            var status = fallbackOpen.Drives.Single();
            Assert.AreEqual(DriveState.Ready, status.State);
            Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
            Assert.IsTrue(fallbackOpen.TryGetDriveOrdinal('T', out var ordinal));
            privateBlockPath = fallbackOpen.CurrentSnapshot[ordinal].Block.Path;
            Assert.Contains("mftlib-private-", privateBlockPath);
            Assert.AreNotEqual(CanonicalPath, privateBlockPath);
            Assert.IsNotNull(fallbackOpen.Find(Path.Combine(TreeRoot, "Documents", "readme.md")));
            Assert.AreSequenceEqual(canonicalBytesBeforeRace, await SharedFileReader.ReadAllBytesAsync(CanonicalPath), "a non-cache-only open racing a parked delete must fall back to a private block "
                + "instead of touching the slot the delete still holds");
        }
        Assert.IsFalse(File.Exists(privateBlockPath), "the fallback's private block is delete-on-close");

        gate.Release();
        var results = await deleteTask;
        var result = results.Single();
        Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, result.Outcome);

        Assert.IsFalse(File.Exists(CanonicalPath), "the clear completes once it releases the lock");
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(CanonicalPath)), "the lock file itself is never deleted");

        await using var freshOpen = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var freshStatus = freshOpen.Drives.Single();
        Assert.AreEqual(DriveState.Ready, freshStatus.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, freshStatus.Block!.Origin);
        Assert.IsTrue(freshOpen.TryGetDriveOrdinal('T', out var freshOrdinal));
        Assert.AreEqual(CanonicalPath, freshOpen.CurrentSnapshot[freshOrdinal].Block.Path,
            "once the clear has released the lock, a fresh open takes the slot normally");
    }
}
