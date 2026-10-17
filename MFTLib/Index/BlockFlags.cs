namespace MFTLib.Index;

/// <summary>Header state: producers write <see cref="Complete" /> last; absent completion requires discard and rescan. <see cref="CapacityExhausted" /> means a write did not fit and needs a rescan.</summary>
[Flags]
internal enum BlockFlags : uint
{
    /// <summary>No header-level state is set.</summary>
    None = 0,
    /// <summary>Set by the producer after writing completes and before flushing; a block without it is discarded.</summary>
    Complete = 1,
    /// <summary>A producer write or journal mutation could not fit; rescan to rebuild the block.</summary>
    CapacityExhausted = 2
}
