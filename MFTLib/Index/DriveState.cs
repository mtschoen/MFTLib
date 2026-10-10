namespace MFTLib.Index;

/// <summary>
///     Whether a drive has a queryable block. <see cref="Ready" /> includes blocks needing
///     compaction, reported independently by <see cref="DriveBlockStatus.CompactionNeeded" />.
///     <see cref="Offline" /> means the drive was unavailable at open; <see cref="Failed" />
///     means its producer failed, a cache-only open found no usable block or a mismatched
///     cache tag, or its cache slot was in use, distinguished by <see cref="DriveStatus.FailureKind" />.
///     No handles are minted for an offline or failed drive.
/// </summary>
public enum DriveState
{
    /// <summary>The drive has a valid block and answers queries; its watch may still be failing independently.</summary>
    Ready,

    /// <summary>The drive was unavailable when the index opened and has no block.</summary>
    Offline,

    /// <summary>
    ///     The drive has no block: its MFT producer failed, a cache-only open found no usable
    ///     cache, the cache tag did not match, or the cache block is owned by another live index.
    ///     <see cref="DriveStatus.FailureKind" /> says which, and
    ///     <see cref="DriveStatus.FailureMessage" /> carries the detail.
    /// </summary>
    Failed
}
