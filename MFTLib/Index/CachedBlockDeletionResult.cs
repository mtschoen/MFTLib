namespace MFTLib.Index;

/// <summary>A point-in-time deletion result, not a reservation of the cache slot.</summary>
public sealed record CachedBlockDeletionResult
{
    /// <summary>Creates a deletion result.</summary>
    /// <param name="file">The inventory identity and metadata captured before deletion.</param>
    /// <param name="outcome">Whether deletion succeeded, ownership was unavailable, or deletion failed.</param>
    /// <param name="failureReason">The filesystem error message for Failed; otherwise null.</param>
    internal CachedBlockDeletionResult(CachedBlockFile file, CachedBlockDeletionOutcome outcome,
        string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(file);
        File = file;
        Outcome = outcome;
        FailureReason = failureReason;
    }

    /// <summary>The block as it was inventoried before the deletion attempt; deletion does not refresh it.</summary>
    public CachedBlockFile File { get; init; }

    /// <summary>Whether deletion succeeded, ownership was unavailable, or deletion failed.</summary>
    public CachedBlockDeletionOutcome Outcome { get; init; }

    /// <summary>The filesystem error message for Failed; otherwise null.</summary>
    public string? FailureReason { get; init; }
}
