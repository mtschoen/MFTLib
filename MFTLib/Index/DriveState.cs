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
    /// <summary>The drive has a valid block and answers queries; its watch may still be failing independently.</summary>
    Ready,

    /// <summary>A mutation did not fit the block, so queries still answer but a rescan is needed.</summary>
    Stale,

    /// <summary>The drive was unavailable when the index opened and has no block.</summary>
    Offline,

    /// <summary>
    ///     The drive has no block: its MFT producer failed, a cache-only open found no usable
    ///     cache, or the cache block is owned by another live index.
    ///     <see cref="DriveStatus.FailureKind" /> says which, and
    ///     <see cref="DriveStatus.MftProducerFailureMessage" /> carries the detail.
    /// </summary>
    Failed
}
