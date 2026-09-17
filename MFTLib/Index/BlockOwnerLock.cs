namespace MFTLib.Index;

/// <summary>
///     The owner lock for one canonical cache block: a sibling "<c>.lock</c>" file held open
///     with <see cref="FileShare.None" /> for as long as an index owns the cache slot. On
///     Windows the share mode is mandatory, so a second opener's open fails with a sharing
///     violation; on Unix the runtime translates <see cref="FileShare.None" /> into an
///     exclusive non-blocking flock, which fails the same way against any cooperating process.
///     Both are released by the operating system when the holding process dies, so a killed
///     index never strands its cache slot. The file itself is never deleted: unlinking it after
///     release races another opener creating and locking a fresh file, which would give two
///     indexes the same slot, and a leftover lock file matches no block-file pattern.
/// </summary>
internal sealed class BlockOwnerLock : IDisposable
{
    readonly FileStream _stream;

    BlockOwnerLock(FileStream stream)
    {
        _stream = stream;
    }

    public static string LockPathFor(string canonicalBlockPath) => canonicalBlockPath + ".lock";

    /// <summary>
    ///     Takes the lock for <paramref name="canonicalBlockPath" />'s cache slot, or returns
    ///     null when another live index holds it. An unreadable lock file is treated the same
    ///     fail-safe way: what cannot be locked is never validated, renamed, or deleted.
    /// </summary>
    public static BlockOwnerLock? TryAcquire(string canonicalBlockPath)
    {
        try
        {
            return new BlockOwnerLock(new FileStream(LockPathFor(canonicalBlockPath),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            // Held by another live index (sharing violation or flock conflict), or the cache
            // directory is unreadable; both mean "not ours".
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Same fail-safe mapping as the IOException case above.
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
