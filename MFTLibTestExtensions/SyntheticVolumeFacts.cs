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
        volume?.Validated();
        if (volume is null != volumeRootDirectory is null)
        {
            throw new ArgumentException(
                "Set volume and volumeRootDirectory together, or neither.", nameof(volumeRootDirectory));
        }

        var driveType = isLocal ? DriveType.Fixed : DriveType.Network;
        var format = isNtfs ? "NTFS" : "exFAT";
        return options with
        {
            VolumeFacts = (root, rootDirectory) =>
            {
                var (letter, volumeRoot) = Locate(root, rootDirectory, volume, volumeRootDirectory);
                return new VolumeFacts(true, letter, volumeRoot, volumeSerial, true, driveType, format);
            }
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
