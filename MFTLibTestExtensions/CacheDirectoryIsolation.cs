using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Opt-in protection against tests resolving the real per-user cache directory.</summary>
public static class CacheDirectoryIsolation
{
    /// <summary>
    ///     Forbids <see cref="CacheDirectory.ResolveDefaultPath" /> for the remainder
    ///     of this test process. Call from the test assembly's module initializer,
    ///     before opening any indexes. Repeated calls are safe; there is no reset.
    /// </summary>
    /// <remarks>
    ///     Guarded resolution throws <see cref="InvalidOperationException" />.
    ///     Supply a temporary path through <see cref="FileIndexOptions.CacheDirectory" />.
    ///     Activation is in-process and is not inherited by child processes.
    /// </remarks>
    public static void ForbidDefaultCacheDirectory()
    {
        CacheDirectory.ForbidDefaultCacheDirectory();
    }
}
