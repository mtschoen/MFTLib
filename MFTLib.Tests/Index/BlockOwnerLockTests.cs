using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="BlockOwnerLock" /> acquisition: one owner per cache slot, enforced the same
///     way on every platform (the Windows share mode and the Unix flock answer a second opener
///     identically), released by dispose, and observable as a sibling file.
/// </summary>
[TestClass]
public class BlockOwnerLockTests
{
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A just-released lock file can stay visible briefly on Windows.
        }
    }

    [TestMethod]
    public void TryAcquire_SecondAcquisitionOnTheSamePathFailsUntilTheFirstIsDisposed()
    {
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));

        var first = BlockOwnerLock.TryAcquire(blockPath);
        Assert.IsNotNull(first);
        Assert.AreEqual(blockPath + ".lock", BlockOwnerLock.LockPathFor(blockPath));
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(blockPath)));

        Assert.IsNull(BlockOwnerLock.TryAcquire(blockPath),
            "a second opener must observe the held lock, same process or not");

        first.Dispose();
        using var reacquired = BlockOwnerLock.TryAcquire(blockPath);
        Assert.IsNotNull(reacquired, "disposing the owner releases the slot for the next index");
    }

    [TestMethod]
    public void TryAcquire_AnUnreadableLockFile_ReportsInUseRatherThanThrowing()
    {
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        var lockPath = BlockOwnerLock.LockPathFor(blockPath);
        Directory.CreateDirectory(lockPath);

        try
        {
            // A directory at the lock-file path denies the file open (UnauthorizedAccessException),
            // which the fail-safe mapping reports as "in use" rather than throwing. Using a
            // directory rather than a read-only file ensures the open fails even when the test
            // runs as root in containerized Linux CI (where root bypasses read-only permission bits).
            Assert.IsNull(BlockOwnerLock.TryAcquire(blockPath));
        }
        finally
        {
            if (Directory.Exists(lockPath))
            {
                Directory.Delete(lockPath);
            }
        }
    }
}
