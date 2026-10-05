namespace MFTLib.Index;

/// <summary>
///     One open-time notification from <see cref="FileIndex.OpenAsync" />: the named drive has
///     settled, whatever the outcome. Each drive that settles reports once, from the thread that
///     settled it; a drive whose settle is cancelled reports nothing, so a cancelled or failed open
///     may have reported only some of the configured drives. Drives settle concurrently and no
///     lock is held while a handler runs, so reports can overlap and arrive out of order:
///     <see cref="SettledCount" /> gives the order, and a consumer rendering "3 of 9 drives
///     settled" keeps the report with the largest count rather than the last one received.
/// </summary>
public sealed record IndexDriveOpened
{
    /// <summary>The settled drive, in upper case, so it matches <see cref="DriveStatus.DriveLetter" /> of a drive configured with an upper-case letter.</summary>
    public required char DriveLetter { get; init; }

    /// <summary>
    ///     This drive was the <see cref="SettledCount" />-th of the open's drives to settle, counted
    ///     from 1. Claimed when the drive's final open state is recorded, so it is settle order,
    ///     and for drives with a block it is also block ordinal order. It is not the drive's
    ///     position in <see cref="FileIndexOptions.Drives" />.
    /// </summary>
    public required int SettledCount { get; init; }

    /// <summary>How many drives this open was configured with.</summary>
    public required int Total { get; init; }
}
