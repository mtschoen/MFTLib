namespace MFTLib.Index;

/// <summary>
///     Why the MFT could not serve a root, as <c>RootStatus.MftUnavailable</c>. A consumer branches on the
///     value and renders its own words. The values come from the volume facts read once at open, in the order the
///     library checks them, and from a broker launch that failed.
/// </summary>
public enum MftUnavailableReason : uint
{
    /// <summary>The host runs an operating system whose file systems have no MFT, so only a walk can index anything.</summary>
    PlatformWithoutMft = 1,

    /// <summary>The volume could not be read as ready: no media, or the volume refused or vanished mid-read.</summary>
    VolumeNotReady = 2,

    /// <summary>
    ///     The root is not on a local lettered volume: a network share, an optical drive, a mount point without a drive
    ///     letter, or another drive type that is neither fixed nor removable.
    /// </summary>
    NotLocalVolume = 3,

    /// <summary>The volume is local and ready but formatted with a file system other than NTFS, which keeps no MFT.</summary>
    NotNtfs = 4,

    /// <summary><see cref="FileIndexOptions.MftSource" /> is null and the open has to produce a block; a cached block still opens.</summary>
    NoMftSource = 5,

    /// <summary>The elevated broker could not be reached: it ended, or never connected in time.</summary>
    BrokerUnavailable = 6,

    /// <summary>The user declined the elevation prompt that launches the broker, so nothing could read the MFT.</summary>
    ElevationDeclined = 7
}
