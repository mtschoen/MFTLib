namespace MFTLib.Index;

/// <summary>
///     Everything needed to lay out a new block file. Capacities are final: a block never
///     grows, and exhausting either one sets the compaction-needed flag instead.
/// </summary>
public sealed record BlockFileCreateOptions
{
    public required string Path { get; init; }

    public required uint VolumeSerial { get; init; }

    public required ProducerKind ProducerKind { get; init; }

    /// <summary>
    ///     Row index of the volume root. Leave at zero for an enumeration producer, which
    ///     writes its root at row 0; the MFT producer sets 5.
    /// </summary>
    public uint RootRow { get; init; }

    /// <summary>Rows the region can hold. Use <see cref="BlockLayout.ComputeSlotCapacity" />.</summary>
    public required uint SlotCapacity { get; init; }

    /// <summary>Name pool bytes. Use <see cref="BlockLayout.ComputeNamePoolCapacity" />.</summary>
    public required uint NamePoolCapacity { get; init; }

    /// <summary>
    ///     No-cache mode: the file is removed when the mapping closes, so whole-volume metadata
    ///     never persists on disk.
    /// </summary>
    public bool DeleteOnClose { get; init; }

    /// <summary>
    ///     Receives the log line when a block whose creation or header initialization failed is
    ///     deleted. Null (the default) logs nothing. Forwarded from
    ///     <see cref="FileIndexOptions.Diagnostics" /> by the index; producers may leave it null.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>
    ///     The consumer identity to record in the header; a mismatch on a later warm-start
    ///     attempt makes <see cref="BlockValidationResult.WrongCacheTag" /> the outcome instead
    ///     of <see cref="BlockValidationResult.Valid" />. Written before the block is marked
    ///     complete.
    /// </summary>
    public CacheTag CacheTag { get; init; }
}
