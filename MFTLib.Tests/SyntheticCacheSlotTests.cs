using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The public cache-slot ownership fixture of MFTLibTestExtensions: every member is exercised here and
///     judged by <see cref="CacheDirectory.InspectCached" />, the production reader of slot ownership.
/// </summary>
[TestClass]
public sealed class SyntheticCacheSlotTests
{
    const uint Serial = 0x1234;
    static readonly DriveKey Drive = new('T');
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-slot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_cacheDirectory, recursive: true);

    string WriteCorruptBlock()
    {
        var path = SyntheticBlock.CachedPath(_cacheDirectory, Drive, Serial);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    [TestMethod]
    public void Acquire_HeldCorruptBlockIsInUseUntilDisposedThenInvalid()
    {
        var blockPath = WriteCorruptBlock();
        var before = File.ReadAllBytes(blockPath);
        using (var slot = SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial))
        {
            Assert.AreEqual(blockPath, slot.BlockPath);
            var busy = CacheDirectory.InspectCached(_cacheDirectory).Single();
            Assert.AreEqual(blockPath, busy.File.Path);
            Assert.AreEqual(CachedBlockAvailability.InUse, busy.Availability);
        }

        Assert.AreEqual(CachedBlockAvailability.Invalid,
            CacheDirectory.InspectCached(_cacheDirectory).Single().Availability);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(blockPath));
    }

    [TestMethod]
    public void Acquire_DoesNotRequireABlockAndNeverCreatesOne()
    {
        using var slot = SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial);

        Assert.IsFalse(File.Exists(slot.BlockPath));
        Assert.IsTrue(File.Exists(slot.LockPath));
    }

    [TestMethod]
    public void Acquire_WhileAnotherSlotHoldsItThrowsAndTheOwnerKeepsIt()
    {
        WriteCorruptBlock();
        using var slot = SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial));
        Assert.AreEqual(CachedBlockAvailability.InUse,
            CacheDirectory.InspectCached(_cacheDirectory).Single().Availability);
    }

    [TestMethod]
    public void Acquire_AfterDisposeSucceedsAgain()
    {
        SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial).Dispose();

        using var again = SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial);

        Assert.IsTrue(File.Exists(again.LockPath));
    }

    [TestMethod]
    public void Dispose_KeepsTheLockFileAndIsIdempotent()
    {
        var slot = SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial);
        var lockPath = slot.LockPath;

        slot.Dispose();
        slot.Dispose();

        Assert.IsTrue(File.Exists(lockPath));
    }

    [TestMethod]
    public void Acquire_CreatesAMissingCacheDirectory()
    {
        var nested = Path.Combine(_cacheDirectory, "nested");

        using var slot = SyntheticCacheSlot.Acquire(nested, Drive, Serial);

        Assert.IsTrue(File.Exists(slot.LockPath));
    }

    [TestMethod]
    public void LockPathFor_NamesTheSiblingOfTheBlockTheSlotHolds()
    {
        var blockPath = SyntheticBlock.CachedPath(_cacheDirectory, Drive, Serial);
        using var slot = SyntheticCacheSlot.Acquire(_cacheDirectory, Drive, Serial);

        Assert.AreEqual(slot.LockPath, SyntheticCacheSlot.LockPathFor(blockPath));
        Assert.AreNotEqual(blockPath, slot.LockPath);
    }

    [TestMethod]
    public void LockPathFor_NamesTheFileInspectionLeavesBehind()
    {
        var blockPath = WriteCorruptBlock();
        CacheDirectory.InspectCached(_cacheDirectory);

        Assert.IsTrue(File.Exists(SyntheticCacheSlot.LockPathFor(blockPath)));
    }

    [TestMethod]
    public void ArgumentValidation_RejectsEmptyPaths()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SyntheticCacheSlot.Acquire("", Drive, Serial));
        Assert.ThrowsExactly<ArgumentException>(() => SyntheticCacheSlot.LockPathFor(""));
    }
}
