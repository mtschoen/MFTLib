namespace MFTLib.Index;

public enum CachedBlockAvailability
{
    /// <summary>Validation passed under the slot lock, so the root and producer fields are populated.</summary>
    Available,
    /// <summary>The slot lock could not be acquired; no block bytes were read.</summary>
    InUse,
    /// <summary>The slot was locked, but the block was rejected by validation.</summary>
    Invalid
}
