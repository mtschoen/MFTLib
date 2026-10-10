namespace MFTLib.Index;

/// <summary>
///     What one enumeration walk produced. A non-zero
///     <paramref name="AccessDeniedSubtreeCount" /> becomes the drive's warning; a true
///     <paramref name="CompactionNeeded" /> means the block was too small and needs a rescan.
/// </summary>
internal sealed record EnumerationResult(
    uint RowCount,
    int AccessDeniedSubtreeCount,
    bool CompactionNeeded);
