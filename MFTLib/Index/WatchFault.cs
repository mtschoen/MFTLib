namespace MFTLib.Index;

/// <summary>Identifies which boundary raised a live watch fault.</summary>
public enum WatchFaultKind
{
    /// <summary>
    ///     A <see cref="FileIndex.Changed" /> subscriber threw while receiving a batch. The drive
    ///     keeps watching; this is announced once per watch of the drive.
    /// </summary>
    Subscriber,

    /// <summary>
    ///     The drive's own watch failed (its read threw <see cref="DriveWatchFaultException" />).
    ///     The drive's watch has ended.
    /// </summary>
    Drive,

    /// <summary>
    ///     The index could not apply a batch. The block is unchanged, the drive's cursor was not
    ///     advanced, and the drive's watch has ended.
    /// </summary>
    Apply,

    /// <summary>
    ///     The channel carrying the drive's watch was lost, or the watch ended without being
    ///     stopped. The drive's watch has ended.
    /// </summary>
    Channel
}

/// <summary>A fault observed by one drive's watch pump. Every fault names its drive.</summary>
public sealed record WatchFault(WatchFaultKind Kind, char DriveLetter, Exception Exception);
