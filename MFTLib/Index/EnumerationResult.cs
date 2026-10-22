namespace MFTLib.Index;

/// <summary>Walk totals: <paramref name="AccessDeniedSubtreeCount" /> supplies the drive's warning; <paramref name="CapacityExhausted" /> means the block was too small and needs a rescan.</summary>
sealed record EnumerationResult(
    uint RowCount,
    int AccessDeniedSubtreeCount,
    bool CapacityExhausted);
