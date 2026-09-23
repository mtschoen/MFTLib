using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
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
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

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
        var messages = new List<string>();

        var results = CacheDirectory.DeleteCached(_directory, new HashSet<char> { 'T' }, messages.Add);

        CollectionAssert.AreEquivalent(selected, results.Select(result => result.File.Path).ToArray());
        foreach (var result in results)
        {
            Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, result.Outcome);
            Assert.IsNull(result.FailureReason);
            Assert.IsFalse(File.Exists(result.File.Path));
            Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(result.File.Path)));
            using var owner = BlockOwnerLock.TryAcquire(result.File.Path);
            Assert.IsNotNull(owner);
        }
        CollectionAssert.AreEquivalent(selected.Select(path =>
            $"Deleted block file '{path}': clearing a cached block.").ToArray(), messages);
        foreach (var path in new[] { excluded, unrelated, retired, orphanLock })
        {
            Assert.IsTrue(File.Exists(path));
        }
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
        Assert.AreEqual(0, CacheDirectory.DeleteCached(_directory, new HashSet<char>()).Count);
        Assert.AreEqual(0, CacheDirectory.DeleteCached(_directory, new HashSet<char> { 'u' }).Count);
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
    }

    [TestMethod]
    public void DeleteCached_UnopenableLockIsInUseAndDoesNotStopOtherDeletes()
    {
        var unavailable = CreateBlock('T');
        Directory.CreateDirectory(BlockOwnerLock.LockPathFor(unavailable));
        var available = CreateBlock('U');
        var messages = new List<string>();

        var results = CacheDirectory.DeleteCached(_directory, diagnostics: messages.Add);

        Assert.AreEqual(2, results.Count);
        var busy = results.Single(result => result.File.Path == unavailable);
        Assert.AreEqual(CachedBlockDeletionOutcome.InUse, busy.Outcome);
        Assert.IsNull(busy.FailureReason);
        Assert.IsTrue(File.Exists(unavailable));
        Assert.IsTrue(Directory.Exists(BlockOwnerLock.LockPathFor(unavailable)));
        Assert.AreEqual(CachedBlockDeletionOutcome.Deleted,
            results.Single(result => result.File.Path == available).Outcome);
        Assert.AreEqual(1, messages.Count);
    }

    [TestMethod]
    public void DeleteCached_FilesystemFailureIsReportedAndIterationContinues()
    {
        var paths = new[] { CreateBlock('T'), CreateBlock('U'), CreateBlock('V') };
        string? replaced = null;
        var messages = new List<string>();

        var results = CacheDirectory.DeleteCached(_directory, diagnostics: message =>
        {
            messages.Add(message);
            if (replaced is not null)
            {
                return;
            }
            replaced = paths.First(File.Exists);
            File.Delete(replaced);
            Directory.CreateDirectory(replaced);
        });

        Assert.AreEqual(3, results.Count);
        var failure = results.Single(result => result.Outcome == CachedBlockDeletionOutcome.Failed);
        Assert.AreEqual(replaced, failure.File.Path);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.FailureReason));
        Assert.AreEqual(2, results.Count(result => result.Outcome == CachedBlockDeletionOutcome.Deleted));
        Assert.AreEqual(2, messages.Count);
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
        var messages = new List<string>();
        var results = CacheDirectory.DeleteCached(_directory, diagnostics: message =>
        {
            messages.Add(message);
            foreach (var path in paths.Where(File.Exists))
            {
                File.Delete(path);
            }
        });
        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(result => result.Outcome == CachedBlockDeletionOutcome.Deleted));
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public void DeleteCached_CallbackFailurePropagatesAndReleasesOwnership()
    {
        var path = CreateBlock('T');
        var exception = Assert.ThrowsException<IOException>(() =>
            CacheDirectory.DeleteCached(_directory, diagnostics: _ => throw new IOException("observer failed")));
        Assert.AreEqual("observer failed", exception.Message);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(path)));
        using var owner = BlockOwnerLock.TryAcquire(path);
        Assert.IsNotNull(owner);
    }

    [TestMethod]
    public void DeleteCached_EmptyMissingAndInvalidPathsFollowInventoryContract()
    {
        var missing = Path.Combine(_directory, "missing");
        Assert.AreEqual(0, CacheDirectory.DeleteCached(missing).Count);
        Assert.IsFalse(Directory.Exists(missing));
        Assert.AreEqual(0, CacheDirectory.DeleteCached(_directory).Count);
        Assert.ThrowsException<ArgumentNullException>(() => CacheDirectory.DeleteCached(null!));
        Assert.ThrowsException<ArgumentException>(() => CacheDirectory.DeleteCached(string.Empty));
    }
}
