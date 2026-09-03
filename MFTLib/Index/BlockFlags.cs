using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Header-level state. <see cref="Complete" /> is written last by the producer; a block
///     without it was interrupted mid-write and must be discarded and rescanned.
///     <see cref="CompactionNeeded" /> means a mutation could not fit and the drive is stale.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711", Justification = "Flags suffix is conventional here; renaming breaks consumers.")]
public enum BlockFlags : uint
{
    None = 0,
    Complete = 1,
    CompactionNeeded = 2
}
