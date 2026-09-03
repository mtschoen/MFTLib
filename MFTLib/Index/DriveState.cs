namespace MFTLib.Index;

/// <summary>
///     What a drive card should show. <see cref="Stale" /> means a mutation did not fit and the
///     drive needs a rescan; <see cref="Offline" /> means the drive was unavailable at open and
///     no handles are ever minted for it.
/// </summary>
public enum DriveState
{
    Ready,
    Scanning,
    Stale,
    Offline
}
