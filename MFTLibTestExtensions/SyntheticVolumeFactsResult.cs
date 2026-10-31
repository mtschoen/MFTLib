using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Volume facts returned by a <see cref="SyntheticVolumeFacts.Override" /> reader.</summary>
public readonly record struct SyntheticVolumeFactsResult
{
    /// <summary>Creates synthetic volume facts with the same vocabulary and defaults as <see cref="SyntheticVolumeFacts.Apply" />.</summary>
    /// <param name="volumeSerial">The serial the volume reports.</param>
    /// <param name="isNtfs">Whether the volume's file system is NTFS; otherwise it reports exFAT.</param>
    /// <param name="isLocal">Whether the volume is a fixed local drive; otherwise it is a network drive.</param>
    /// <param name="volume">The synthetic volume's drive letter, set together with <paramref name="volumeRootDirectory" />.</param>
    /// <param name="volumeRootDirectory">The host directory the synthetic volume is rooted at.</param>
    public SyntheticVolumeFactsResult(uint volumeSerial, bool isNtfs = true, bool isLocal = true,
        VolumeKey? volume = null, string? volumeRootDirectory = null)
    {
        VolumeSerial = volumeSerial;
        IsNtfs = isNtfs;
        IsLocal = isLocal;
        Volume = volume;
        VolumeRootDirectory = volumeRootDirectory;
    }

    /// <summary>The identity used in cache filenames and volume matching, or zero when no serial is reported.</summary>
    public uint VolumeSerial { get; init; }

    /// <summary>Whether the volume's file system is NTFS; otherwise it reports exFAT.</summary>
    public bool IsNtfs { get; init; }

    /// <summary>Whether the volume is a fixed local drive; otherwise it is a network drive.</summary>
    public bool IsLocal { get; init; }

    /// <summary>The synthetic volume's drive letter, set together with <see cref="VolumeRootDirectory" />.</summary>
    public VolumeKey? Volume { get; init; }

    /// <summary>The host path that makes the synthetic volume's letter resolve to a real directory tree.</summary>
    public string? VolumeRootDirectory { get; init; }
}
