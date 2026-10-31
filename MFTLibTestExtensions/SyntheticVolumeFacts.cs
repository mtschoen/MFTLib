using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Gives an index open test-controlled volume facts. The library reads the facts of the volume under a root once
///     at open to pick the producer; a test that needs a root on an NTFS volume, a different serial or a volume it
///     does not have supplies them here instead of touching the host's drives.
/// </summary>
public static class SyntheticVolumeFacts
{
    /// <summary>
    ///     Returns <paramref name="options" /> whose volume facts are the arguments: a ready volume on a platform with
    ///     an MFT, with the given serial. With both <paramref name="volume" /> and <paramref name="volumeRootDirectory" />
    ///     set, synthetic volume <paramref name="volume" /> is rooted at the host directory
    ///     <paramref name="volumeRootDirectory" />: a volume key for it resolves there, and a directory key under that
    ///     host path belongs to it. With both unset the facts describe the real volume of <see cref="FileIndexOptions.Root" />.
    /// </summary>
    /// <param name="options">The options to open with.</param>
    /// <param name="volumeSerial">The serial the volume reports.</param>
    /// <param name="isNtfs">Whether the volume's file system is NTFS; otherwise it reports exFAT.</param>
    /// <param name="isLocal">Whether the volume is a fixed local drive; otherwise it is a network drive.</param>
    /// <param name="volume">The synthetic volume's drive letter, set together with <paramref name="volumeRootDirectory" />.</param>
    /// <param name="volumeRootDirectory">The host directory the synthetic volume is rooted at.</param>
    /// <returns>A copy of <paramref name="options" /> carrying the facts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    /// <exception cref="ArgumentException">Only one of <paramref name="volume" /> and <paramref name="volumeRootDirectory" /> is set.</exception>
    public static FileIndexOptions Apply(FileIndexOptions options, uint volumeSerial, bool isNtfs = true,
        bool isLocal = true, VolumeKey? volume = null, string? volumeRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            VolumeFacts = CreateReader(volumeSerial, isNtfs, isLocal, volume, volumeRootDirectory)
        };
    }

    /// <summary>
    ///     Permits <see cref="Override" /> for the remainder of this process's life. Call from the test assembly's
    ///     module initializer, before opening any indexes, so the process-global seam cannot be installed by a
    ///     process that never opted in. Repeated calls are safe; there is no reset. Activation is in-process and is
    ///     not inherited by child processes.
    /// </summary>
    public static void AllowOverrides()
    {
        RootResolution.Hooks.AllowVolumeFactsOverrides();
    }

    /// <summary>Supplies synthetic volume facts to index opens until the returned scope is disposed.</summary>
    /// <param name="reader">
    ///     Called with each opened root and its resolved host directory. Return null to read that root's platform facts.
    /// </param>
    /// <returns>A process-global scope that restores platform reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <see cref="AllowOverrides" /> has not been called in this process, or another synthetic volume-facts
    ///     override scope is active.
    /// </exception>
    /// <remarks>
    ///     Facts supplied to an individual options object through <see cref="Apply" /> take precedence over this override.
    ///     The test assembly's module initializer must have called <see cref="AllowOverrides" />, matching
    ///     <see cref="JournalIsolation.ForbidLiveJournalReads" />.
    ///     Use a nonparallel fixture for the entire scope lifetime, including awaited work, and dispose indexes before
    ///     disposing the scope. Readers may run concurrently for different opens; synchronize mutable observations.
    ///     Nested and overlapping scopes are rejected. Repeated disposal is harmless and does not drain active readers.
    /// </remarks>
    public static IDisposable Override(Func<RootKey, string, SyntheticVolumeFactsResult?> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        RequireActivation(RootResolution.Hooks);
        return ProcessGlobalScope.Acquire("A synthetic volume-facts override", () =>
        {
            RootResolution.Hooks.VolumeFactsOverride = (root, rootDirectory) =>
                reader(root, rootDirectory) is { } result
                    ? CreateReader(result.VolumeSerial, result.IsNtfs, result.IsLocal,
                        result.Volume, result.VolumeRootDirectory)(root, rootDirectory)
                    : null;
            return () => RootResolution.Hooks.VolumeFactsOverride = null;
        });
    }

    internal static void RequireActivation(RootResolution.RootResolutionHooks hooks)
    {
        if (!hooks.VolumeFactsOverridesAllowed)
        {
            throw new InvalidOperationException(
                "SyntheticVolumeFacts.AllowOverrides must be called from the test assembly's module initializer before Override.");
        }
    }

    static VolumeFactsReader CreateReader(uint volumeSerial, bool isNtfs, bool isLocal,
        VolumeKey? volume, string? volumeRootDirectory)
    {
        volume?.Validated();
        if (volume is null != volumeRootDirectory is null)
        {
            throw new ArgumentException(
                "Set volume and volumeRootDirectory together, or neither.", nameof(volumeRootDirectory));
        }

        var driveType = isLocal ? DriveType.Fixed : DriveType.Network;
        var format = isNtfs ? "NTFS" : "exFAT";
        return (root, rootDirectory) =>
        {
            var (letter, volumeRoot) = Locate(root, rootDirectory, volume, volumeRootDirectory);
            return new VolumeFacts(true, letter, volumeRoot, volumeSerial, true, driveType, format);
        };
    }

    static (VolumeKey? Letter, string? VolumeRoot) Locate(RootKey root, string rootDirectory, VolumeKey? mappedVolume,
        string? mappedRoot)
    {
        if (mappedVolume is { } mapped && mappedRoot is not null &&
            ((root.TryGetVolume(out var rootVolume) && rootVolume == mapped) || IsUnder(rootDirectory, mappedRoot)))
        {
            return (mapped, mappedRoot);
        }

        if (root.TryGetVolume(out var own))
        {
            return (own, own.Letter + @":\");
        }

        if (rootDirectory is [var letter, ':', ..] && char.IsAsciiLetter(letter))
        {
            var drive = new VolumeKey(letter);
            return (drive, drive.Letter + @":\");
        }

        return (null, null);
    }

    static bool IsUnder(string path, string directory)
    {
        // The library's own path rules, so the synthetic facts and the library cannot disagree about containment.
        var comparison = RootKind.DirectoryWalk.Comparison;
        var trimmed = Path.TrimEndingDirectorySeparator(directory);
        return path.Equals(trimmed, comparison) ||
               (path.StartsWith(trimmed, comparison) && path.Length > trimmed.Length &&
                (path[trimmed.Length] == Path.DirectorySeparatorChar || path[trimmed.Length] == Path.AltDirectorySeparatorChar));
    }
}
