namespace MFTLib.Index;

/// <summary>
///     An eager inspection result. The mapping and slot lock have been released before this
///     result reaches the caller; availability is a point-in-time observation, not a lease.
/// </summary>
/// <param name="File">Filename-derived identity and directory-entry metadata.</param>
/// <param name="Availability">Whether the block was available, un-lockable, or invalid.</param>
/// <param name="Validation">Null for InUse, Valid for Available, and the rejection reason for Invalid.</param>
/// <param name="ProducerKind">The validated header's producer, or null when unavailable or invalid.</param>
/// <param name="RootDirectory">A materialized root for a recognized valid producer, otherwise null.</param>
public sealed record CachedBlockStatus(
    CachedBlockFile File,
    CachedBlockAvailability Availability,
    BlockValidationResult? Validation,
    ProducerKind? ProducerKind,
    string? RootDirectory)
{
    /// <summary>The validated block's tag, or null when the block could not be inspected.</summary>
    public CacheTag? CacheTag { get; init; }
}
