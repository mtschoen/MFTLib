namespace MFTLib.Index;

public static partial class CacheDirectory
{
    static int _defaultCacheDirectoryForbidden;

    /// <summary>Returns the per-user default index cache directory path.</summary>
    /// <exception cref="InvalidOperationException">
    ///     Default-cache resolution was forbidden by the test host through
    ///     <c>CacheDirectoryIsolation.ForbidDefaultCacheDirectory</c>.
    ///     Set <see cref="FileIndexOptions.CacheDirectory" /> to a temporary path.
    /// </exception>
    public static string ResolveDefaultPath() => ProcessDefaultPath.Resolve();

    internal static void ForbidDefaultCacheDirectory()
    {
        Interlocked.Exchange(ref _defaultCacheDirectoryForbidden, 1);
    }

    /// <summary>
    ///     The resolver <see cref="ResolveDefaultPath()" /> uses: its guard is this process's
    ///     one-way flag, and nothing can replace it or its guard.
    /// </summary>
    static readonly DefaultPathResolver ProcessDefaultPath = new(
        () => Volatile.Read(ref _defaultCacheDirectoryForbidden) != 0, Environment.GetFolderPath);

    /// <summary>
    ///     Resolves the default cache path from a guard and a folder lookup. The process resolves
    ///     through its own instance; a test builds another instance to exercise the unguarded path
    ///     without touching the process's guard.
    /// </summary>
    internal sealed class DefaultPathResolver(Func<bool> isForbidden, Func<Environment.SpecialFolder, string> folderPath)
    {
        public string Resolve()
        {
            if (isForbidden())
            {
                throw new InvalidOperationException(
                    "CacheDirectoryIsolation.ForbidDefaultCacheDirectory has forbidden default cache resolution. " +
                    "Set FileIndexOptions.CacheDirectory to a temporary path.");
            }

            var applicationData = folderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(applicationData))
            {
                applicationData = Path.Combine(folderPath(Environment.SpecialFolder.UserProfile), ".cache");
            }

            return Path.Combine(applicationData, "MFTLib", "index");
        }
    }
}
