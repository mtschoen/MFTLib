namespace MFTLib.Index;

/// <summary>
///     An eager inspection result. The mapping and slot lock have been released before this
///     result reaches the caller; availability is a point-in-time observation, not a lease.
/// </summary>
public sealed record CachedBlockStatus
{
    /// <summary>Creates an inspection result.</summary>
    /// <param name="file">Filename-derived identity and directory-entry metadata.</param>
    /// <param name="availability">Whether the block was available, un-lockable, or invalid.</param>
    /// <param name="validation">Null for InUse, Valid for Available, and the rejection reason for Invalid.</param>
    /// <param name="producerKind">The validated header's producer, or null when unavailable or invalid.</param>
    /// <param name="rootDirectory">A materialized root for a recognized valid producer, otherwise null.</param>
    internal CachedBlockStatus(CachedBlockFile file, CachedBlockAvailability availability,
        BlockValidationResult? validation, ProducerKind? producerKind, string? rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(file);
        File = file;
        Availability = availability;
        Validation = validation;
        ProducerKind = producerKind;
        RootDirectory = rootDirectory;
    }

    /// <summary>Filename-derived identity and directory-entry metadata.</summary>
    public CachedBlockFile File { get; init; }

    /// <summary>Whether the block was available, un-lockable, or invalid.</summary>
    public CachedBlockAvailability Availability { get; init; }

    /// <summary>Null for InUse, Valid for Available, and the rejection reason for Invalid.</summary>
    public BlockValidationResult? Validation { get; init; }

    /// <summary>The validated header's producer, or null when unavailable or invalid.</summary>
    public ProducerKind? ProducerKind { get; init; }

    /// <summary>A materialized root for a recognized valid producer, otherwise null.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>The validated block's tag, or null when the block could not be inspected.</summary>
    public CacheTag? CacheTag { get; init; }
}
