namespace MFTLib.Index;

/// <summary>The cache location backing a root's current published block.</summary>
public enum CacheLocation
{
    /// <summary>The persistent canonical cache file held under this session's exclusive lock.</summary>
    OwnedCanonical,

    /// <summary>A private block because the canonical location was unavailable; retained until a canonical rescan publishes.</summary>
    PrivateFallback
}
