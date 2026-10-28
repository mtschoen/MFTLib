using MFTLib.NativeInterop;

namespace MFTLib.Index;

/// <summary>
///     What resolving a directory's final path produced: the path with every link followed, or the reason it could not
///     be read (the open then falls back to the path as named and reports the reason once).
/// </summary>
/// <param name="Path">The final path, or null when resolution failed.</param>
/// <param name="Failure">Why resolution failed, naming the platform error; null on success.</param>
internal readonly record struct FinalPathResolution(string? Path, string? Failure);

/// <summary>Resolves an existing directory to its final path, following junctions, symbolic links and mount points.</summary>
internal delegate FinalPathResolution FinalPathResolver(string directory);

/// <summary>The production <see cref="FinalPathResolver" />.</summary>
static class PlatformFinalPath
{
    /// <summary>
    ///     Windows opens the directory following links and reads its normalized DOS final path; elsewhere the C library
    ///     resolves every component. A refusal from the platform is a failed resolution, never an exception.
    /// </summary>
    internal static FinalPathResolution Resolve(string directory)
    {
        try
        {
            return OperatingSystem.IsWindows() ? ResolveOnWindows(directory) : ResolveWithRealPath(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return new FinalPathResolution(null, $"{exception.Message} (error {exception.HResult & 0xFFFF})");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static FinalPathResolution ResolveOnWindows(string directory)
    {
        using var handle = HandleBoundFile.OpenDirectoryFollowingLinks(directory);
        return new FinalPathResolution(StripDosPrefix(HandleBoundFile.FinalPathOf(handle)), null);
    }

    static FinalPathResolution ResolveWithRealPath(string directory)
    {
        var resolved = PosixPath.RealPath(directory, out var error);
        return resolved is null
            ? new FinalPathResolution(null, $"realpath failed (error {error})")
            : new FinalPathResolution(resolved, null);
    }

    /// <summary>Maps <c>\\?\C:\x</c> to <c>C:\x</c> and <c>\\?\UNC\server\share</c> to <c>\\server\share</c>.</summary>
    internal static string StripDosPrefix(string finalPath)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string dosPrefix = @"\\?\";
        if (finalPath.StartsWith(uncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + finalPath[uncPrefix.Length..];
        }

        return finalPath.StartsWith(dosPrefix, StringComparison.Ordinal) ? finalPath[dosPrefix.Length..] : finalPath;
    }
}
