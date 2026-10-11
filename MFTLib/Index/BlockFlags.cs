namespace MFTLib.Index;

/// <summary>
///     Header-level state. <see cref="Complete" /> is written last by the producer; a block
///     without it was interrupted mid-write and must be discarded and rescanned.
///     <see cref="CompactionNeeded" /> means a mutation could not fit and the block needs a rescan.
/// </summary>
[Flags]
internal enum BlockFlags : uint
{
    /// <summary>No header-level state is set.</summary>
    None = 0,
    /// <summary>Set by the producer after writing completes and before flushing; a block without it is discarded.</summary>
    Complete = 1,
    /// <summary>A journal mutation could not fit in the block, so it requires compaction.</summary>
    CompactionNeeded = 2
}
