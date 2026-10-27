using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class CacheDirectoryDeletionTests
{
    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        CacheDirectory.Hooks.ResetToDefaults();
        Directory.Delete(_directory, recursive: true);
    }

    string CreateBlock(char letter, uint serial = 1)
    {
        var path = Path.Combine(_directory, CacheDirectory.BlockFileName(letter, serial));
        File.WriteAllText(path, "deliberately invalid block content");
        return path;
    }

    [TestMethod]
    public void DeleteCached_DeletesSelectedSerialsAndPreservesOtherEntries()
    {
        var selected = new[] { CreateBlock('T'), CreateBlock('T', 2) };
        var excluded = CreateBlock('U');
        var unrelated = Path.Combine(_directory, "notes.mlix");
        var retired = selected[0] + ".retired-test";
        var orphanLock = Path.Combine(_directory, "V-00000001.mlix.lock");
        foreach (var path in new[] { unrelated, retired, orphanLock })
        {
            File.WriteAllText(path, "keep");
        }

        var results = CacheDirectory.DeleteCached(_directory,
            new HashSet<char> { 'T' }.Select(letter => new DriveKey(letter)));

        Assert.AreSequenceEqual(selected, [.. results.Select(result => result.File.Path)], SequenceOrder.InAnyOrder);
        foreach (var result in results)
        {
            Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, result.Outcome);
            Assert.IsNull(result.FailureMessage);
            Assert.IsFalse(File.Exists(result.File.Path));
            Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(result.File.Path)));
            using var owner = BlockOwnerLock.TryAcquire(result.File.Path);
            Assert.IsNotNull(owner);
        }
        foreach (var path in new[] { excluded, unrelated, retired, orphanLock })
        {
            Assert.IsTrue(File.Exists(path));
        }
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
        Assert.IsEmpty(CacheDirectory.DeleteCached(_directory, new HashSet<char>().Select(letter => new DriveKey(letter))));
        Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, CacheDirectory.DeleteCached(_directory, ['u']).Single().Outcome);
        Assert.IsFalse(File.Exists(excluded));
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
    }

    [TestMethod]
    public void DeleteCached_UnopenableLockIsInUseAndDoesNotStopOtherDeletes()
    {
        var unavailable = CreateBlock('T');
        Directory.CreateDirectory(BlockOwnerLock.LockPathFor(unavailable));
        var available = CreateBlock('U');

        var results = CacheDirectory.DeleteCached(_directory);

        Assert.HasCount(2, results);
        var busy = results.Single(result => result.File.Path == unavailable);
        Assert.AreEqual(CachedBlockDeletionOutcome.InUse, busy.Outcome);
        Assert.IsNull(busy.FailureMessage);
        Assert.IsTrue(File.Exists(unavailable));
        Assert.IsTrue(Directory.Exists(BlockOwnerLock.LockPathFor(unavailable)));
        Assert.AreEqual(CachedBlockDeletionOutcome.Deleted,
            results.Single(result => result.File.Path == available).Outcome);
    }

    [TestMethod]
    public void DeleteCached_FilesystemFailureIsReportedAndIterationContinues()
    {
        var paths = new[] { CreateBlock('T'), CreateBlock('U'), CreateBlock('V') };
        string? replaced = null;
        CacheDirectory.Hooks._beforeDelete = path =>
        {
            if (replaced is not null)
            {
                return;
            }

            replaced = path;
            File.Delete(path);
            Directory.CreateDirectory(path);
        };

        var results = CacheDirectory.DeleteCached(_directory);

        Assert.HasCount(3, results);
        var failure = results.Single(result => result.Outcome == CachedBlockDeletionOutcome.Failed);
        Assert.AreEqual(replaced, failure.File.Path);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.FailureMessage));
        Assert.AreEqual(2, results.Count(result => result.Outcome == CachedBlockDeletionOutcome.Deleted));
        Assert.IsTrue(Directory.Exists(replaced));
        foreach (var path in paths)
        {
            Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(path)));
            using var owner = BlockOwnerLock.TryAcquire(path);
            Assert.IsNotNull(owner);
        }
    }

    [TestMethod]
    public void DeleteCached_AlreadyAbsentInventoryEntryIsAnIdempotentSuccess()
    {
        var paths = new[] { CreateBlock('T'), CreateBlock('U') };
        CacheDirectory.Hooks._beforeDelete = File.Delete;

        var results = CacheDirectory.DeleteCached(_directory);

        Assert.AreSequenceEqual(paths, [.. results.Select(result => result.File.Path)], SequenceOrder.InAnyOrder);
        foreach (var result in results)
        {
            Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, result.Outcome);
            Assert.IsNull(result.FailureMessage);
            Assert.IsFalse(File.Exists(result.File.Path));
            Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(result.File.Path)));
            using var owner = BlockOwnerLock.TryAcquire(result.File.Path);
            Assert.IsNotNull(owner);
        }
    }

    [TestMethod]
    public void DeleteCached_EmptyMissingAndInvalidPathsFollowInventoryContract()
    {
        var missing = Path.Combine(_directory, "missing");
        Assert.IsEmpty(CacheDirectory.DeleteCached(missing));
        Assert.IsFalse(Directory.Exists(missing));
        Assert.IsEmpty(CacheDirectory.DeleteCached(_directory));
        Assert.ThrowsExactly<ArgumentNullException>(() => CacheDirectory.DeleteCached(null!));
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.DeleteCached(string.Empty));
    }
}
