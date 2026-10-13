namespace MFTLib.Index;

/// <summary>The cache-slot backing of a drive's current published block, when it has one.</summary>
public enum CacheSlotState
{
    /// <summary>
    ///     The drive is backed by the persistent cache file on disk under this session's exclusive lock.
    /// </summary>
    OwnedCanonical,

    /// <summary>
    ///     The block is private because the canonical slot was unavailable when its scan
    ///     target was selected. It stays private until a rescan publishes a canonical block.
    /// </summary>
    PrivateFallback
}
