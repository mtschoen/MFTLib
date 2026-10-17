namespace MFTLib.Index;

/// <summary>
///     One open-time notification from <see cref="FileIndex.OpenAsync" />: the named drive has
///     settled, whatever the outcome. Each drive that settles reports once, from the thread that
///     settled it; a drive whose settle is cancelled reports nothing, so a cancelled or failed open
///     may have reported only some of the configured drives. Drives settle concurrently and no
///     lock is held while a handler runs, so reports can overlap and arrive out of order:
///     <see cref="SettledDriveCount" /> gives the order, and a consumer rendering "3 of 9 drives
///     settled" keeps the report with the largest count rather than the last one received.
/// </summary>
public sealed record IndexDriveOpened
{
    /// <summary>Creates a report that one drive has settled.</summary>
    /// <param name="drive">The settled drive, in upper case.</param>
    /// <param name="settledDriveCount">The settle order of this drive, counted from 1.</param>
    /// <param name="totalDriveCount">How many drives the open was configured with.</param>
    internal IndexDriveOpened(DriveKey drive, int settledDriveCount, int totalDriveCount)
    {
        Drive = drive;
        SettledDriveCount = settledDriveCount;
        TotalDriveCount = totalDriveCount;
    }

    /// <summary>The normalized key of the settled drive, matching <see cref="DriveStatus.Drive" />.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); }

    /// <summary>
    ///     This drive was the <see cref="SettledDriveCount" />-th of the open's drives to settle, counted
    ///     from 1. Claimed when the drive's final open state is recorded, so it is settle order,
    ///     and for drives with a block it is also block ordinal order. It is not the drive's
    ///     position in <see cref="FileIndexOptions.Drives" />.
    /// </summary>
    public int SettledDriveCount { get; init; }

    /// <summary>How many drives this open was configured with.</summary>
    public int TotalDriveCount { get; init; }
}
