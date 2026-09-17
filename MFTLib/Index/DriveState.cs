namespace MFTLib.Index;

/// <summary>
///     What a drive card should show. <see cref="Stale" /> means a mutation did not fit and the
///     drive needs a rescan; <see cref="Offline" /> means the drive was unavailable at open;
///     <see cref="Failed" /> means the drive has no block: its MFT producer failed or a
///     cache-only open declined it, distinguished by <see cref="DriveStatus.FailureKind" />.
///     No handles are minted for an offline or failed drive.
/// </summary>
public enum DriveState
{
    Ready,
    Scanning,
    Stale,
    Offline,

    /// <summary>
    ///     The drive has no block: its MFT producer failed or a cache-only open found no usable
    ///     cache and forbade a scan. <see cref="DriveStatus.FailureKind" /> says which, and
    ///     <see cref="DriveStatus.MftProducerFailureMessage" /> carries the detail.
    /// </summary>
    Failed
}
