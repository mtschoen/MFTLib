namespace MFTLib.Index;

/// <summary>
///     One open-time notification from <see cref="FileIndex.OpenAsync" />: the named drive has
///     settled, whatever the outcome. Reports arrive once per configured drive, in
///     <see cref="FileIndexOptions.Drives" /> order, so a consumer can render "drive 3 of 9: G:"
///     while the open is still in flight. The initial watch catch-up MFTLib#139 describes is a
///     later moment in the same per-drive lifecycle; its notification can follow this record's
///     shape as a sibling rather than introducing a second vocabulary.
/// </summary>
public sealed record IndexDriveOpened
{
    /// <summary>The settled drive, as <see cref="DriveStatus.DriveLetter" /> reports it.</summary>
    public required char DriveLetter { get; init; }

    /// <summary>
    ///     The drive's 1-based position in <see cref="FileIndexOptions.Drives" />. This is the
    ///     configured open order, not the block ordinal a <see cref="DriveBlock" /> carries.
    /// </summary>
    public required int Ordinal { get; init; }

    /// <summary>How many drives this open was configured with.</summary>
    public required int Total { get; init; }

    /// <summary>
    ///     Where the settled drive's block came from, as <see cref="DriveStatus.BlockSource" />
    ///     reports it. <see cref="MFTLib.Index.BlockSource.None" /> for an offline, declined, or
    ///     failed drive.
    /// </summary>
    public required BlockSource BlockSource { get; init; }

    /// <summary>The settled drive's state, as <see cref="DriveStatus.State" /> reports it.</summary>
    public required DriveState State { get; init; }
}
