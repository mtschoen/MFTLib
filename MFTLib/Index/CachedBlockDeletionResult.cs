namespace MFTLib.Index;

/// <summary>A point-in-time deletion result, not a reservation of the cache slot.</summary>
/// <param name="File">The inventory identity and metadata captured before deletion.</param>
/// <param name="Outcome">Whether deletion succeeded, ownership was unavailable, or deletion failed.</param>
/// <param name="FailureReason">The filesystem error message for Failed; otherwise null.</param>
public sealed record CachedBlockDeletionResult(
    CachedBlockFile File,
    CachedBlockDeletionOutcome Outcome,
    string? FailureReason);
