namespace MFTLib.Index;

/// <summary>The result of attempting to own and inspect one cached block.</summary>
public enum CachedBlockAvailability
{
    /// <summary>The block was validated while its slot lock was held.</summary>
    Available,
    /// <summary>The slot lock could not be acquired; no block bytes were read.</summary>
    InUse,
    /// <summary>The slot was locked, but the block was rejected by validation.</summary>
    Invalid
}
