namespace SampleProgram.Watch;

partial class SampleHost
{
    internal Func<string?> _getUserProfileDirectory = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string CacheDirectoryFromProfile(string? profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            throw new ArgumentException("No user profile is available. Set --cache-directory.", nameof(profileDirectory));
        }

        return Path.Combine(profileDirectory, ".MFTLib.Sample.Watch", "cache");
    }

    string ResolveCacheDirectory(WatchArguments parsed) =>
        parsed.CacheDirectory ?? _cacheDirectory ?? CacheDirectoryFromProfile(_getUserProfileDirectory());
}
