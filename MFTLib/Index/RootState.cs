namespace MFTLib.Index;

/// <summary>
///     Whether a root has a queryable block. <see cref="Ready" /> includes blocks with exhausted
///     capacity, reported independently by <see cref="RootBlockStatus.IsCapacityExhausted" />.
///     <see cref="Offline" /> means the root was unavailable at open; <see cref="Failed" />
///     means its producer failed, a cache-only open found no usable block or a mismatched
///     cache tag, or its cache slot was in use, distinguished by <see cref="RootFailure.Kind" />.
///     No handles are minted for an offline or failed root.
/// </summary>
public enum RootState
{
    /// <summary>The root has a valid block and answers queries; its watch may still be failing independently.</summary>
    Ready,

    /// <summary>The root was unavailable when the index opened and has no block.</summary>
    Offline,

    /// <summary>
    ///     The volume has no block: its MFT producer failed, a cache-only open found no usable
    ///     cache, the cache tag did not match, or the cache block's owner lock could not be acquired.
    ///     <see cref="RootStatus.Failure" /> says which and carries the detail.
    /// </summary>
    Failed
}
