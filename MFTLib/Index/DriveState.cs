namespace MFTLib.Index;

/// <summary>
///     What a drive card should show. <see cref="Stale" /> means a mutation did not fit and the
///     drive needs a rescan; <see cref="Offline" /> means the drive was unavailable at open;
///     <see cref="Failed" /> means its MFT producer failed. No handles are minted for an offline
///     or failed drive.
/// </summary>
public enum DriveState
{
    Ready,
    Scanning,
    Stale,
    Offline,

    /// <summary>
    ///     The drive's MFT producer failed, with details in
    ///     <see cref="DriveStatus.MftProducerFailureMessage" />.
    /// </summary>
    Failed
}
