namespace MFTLib.Index;

/// <summary>Header state: producers write <see cref="Complete" /> last; absent completion requires discard and rescan. <see cref="CapacityExhausted" /> means a change could not be stored, even by growing the block.</summary>
[Flags]
enum BlockFlags : uint
{
    /// <summary>No header-level state is set.</summary>
    None = 0,
    /// <summary>Set by the producer after writing completes and before flushing; a block without it is discarded.</summary>
    Complete = 1,
    /// <summary>
    ///     A producer write did not fit at the region clamps, or a journal change was skipped because no growth could
    ///     store it; the block's inventory is incomplete until a rescan rebuilds it. A full region alone never sets it.
    /// </summary>
    CapacityExhausted = 2
}
