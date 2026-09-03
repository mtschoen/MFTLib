namespace MFTLib.Index;

/// <summary>
///     What one enumeration walk produced. A non-zero
///     <paramref name="AccessDeniedSubtreeCount" /> becomes the drive's warning; a true
///     <paramref name="CompactionNeeded" /> means the block was too small and the drive is stale.
/// </summary>
public sealed record EnumerationResult(
    uint RowCount,
    uint NamePoolUsedBytes,
    int AccessDeniedSubtreeCount,
    bool CompactionNeeded);
