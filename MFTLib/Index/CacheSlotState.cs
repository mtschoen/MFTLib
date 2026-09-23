namespace MFTLib.Index;

/// <summary>The cache-slot backing of a drive's current published block.</summary>
public enum CacheSlotState
{
    /// <summary>NoCache is enabled, or the drive has no block.</summary>
    NotApplicable = 0,

    /// <summary>
    ///     The drive is backed by the persistent cache file on disk under this session's exclusive lock.
    /// </summary>
    OwnedCanonical = 1,

    /// <summary>
    ///     The block is private because the canonical slot was unavailable when its scan
    ///     target was selected. It stays private until a rescan publishes a canonical block.
    /// </summary>
    PrivateFallback = 2
}
