using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Holds one cache slot's production owner lock without opening, validating, changing or
///     deleting the block beside it, so a test can observe how an index or a cache command treats a
///     slot that another process owns, whatever the block holds, corrupt or absent included.
///     Disposing releases ownership and leaves the lock file in place, as production does.
///     <see cref="SyntheticBlock" /> takes the same lock only for the length of one seed, edit or read.
/// </summary>
public sealed class SyntheticCacheSlot : IDisposable
{
    readonly BlockOwnerLock _owner;

    SyntheticCacheSlot(BlockOwnerLock owner, string blockPath)
    {
        _owner = owner;
        BlockPath = blockPath;
    }

    /// <summary>The canonical block file of the held slot, as <see cref="SyntheticBlock.CachedPath" /> returns it. The file may not exist.</summary>
    public string BlockPath { get; }

    /// <summary>The sibling lock file of the block: created on first acquisition, present while the slot is held, and still present after release.</summary>
    public string LockPath => LockPathFor(BlockPath);

    /// <summary>The lock file the cache keeps beside a canonical block, for asserting that a cache command leaves it in place.</summary>
    /// <param name="blockPath">The canonical block path, as <see cref="SyntheticBlock.CachedPath" /> or an inspected block's file returns it.</param>
    /// <returns>The full path of the block's lock file.</returns>
    public static string LockPathFor(string blockPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(blockPath);
        return BlockOwnerLock.LockPathFor(blockPath);
    }

    /// <summary>Takes ownership of the drive's cache slot until the returned slot is disposed.</summary>
    /// <param name="cacheDirectory">The cache directory, created when missing.</param>
    /// <param name="drive">The drive letter.</param>
    /// <param name="volumeSerial">The volume serial number the index reports for the drive.</param>
    /// <returns>The held slot.</returns>
    /// <exception cref="InvalidOperationException">An open index or another slot already owns it.</exception>
    public static SyntheticCacheSlot Acquire(string cacheDirectory, RootKey drive, uint volumeSerial)
    {
        var blockPath = SyntheticBlock.CachedPath(cacheDirectory, drive, volumeSerial);
        Directory.CreateDirectory(cacheDirectory);
        var owner = BlockOwnerLock.TryAcquire(blockPath) ?? throw new InvalidOperationException(
            $"The cache slot of {blockPath} is already owned. Dispose the index or slot that holds it first.");
        return new SyntheticCacheSlot(owner, blockPath);
    }

    /// <summary>Releases ownership; the lock file stays. Calling it again does nothing.</summary>
    public void Dispose()
    {
        _owner.Dispose();
    }
}
