namespace SampleProgram.Watch;

partial class SampleHost
{
    internal static string CacheDirectoryFromProfile(string? profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            throw new ArgumentException("No user profile is available. Set --cache-directory.", nameof(profileDirectory));
        }

        return Path.Combine(profileDirectory, ".MFTLib.Sample.Watch", "cache");
    }

    internal static string ResolveCacheDirectory(WatchArguments parsed, string? profileDirectory) =>
        parsed.CacheDirectory ?? CacheDirectoryFromProfile(profileDirectory);
}
