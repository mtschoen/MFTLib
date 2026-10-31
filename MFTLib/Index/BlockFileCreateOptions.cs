namespace MFTLib.Index;

/// <summary>
///     Everything needed to lay out a new block file. Capacities are final: a block never grows;
///     a rescan or a walk replaces it with a larger block, and exhausting any region sets the
///     capacity-exhausted flag instead.
/// </summary>
sealed record BlockFileCreateOptions
{
    /// <summary>Full path of the block file to create.</summary>
    public required string Path { get; init; }

    /// <summary>The root the block indexes, stored in page 0 so the block names its own root.</summary>
    public required RootKey Root { get; init; }

    /// <summary>NTFS volume serial number that the block represents.</summary>
    public required uint VolumeSerial { get; init; }

    /// <summary>Identifies the producer and row-number semantics recorded in the header.</summary>
    public required ProducerKind ProducerKind { get; init; }

    /// <summary>
    ///     Row index of the volume root. Leave at zero for an enumeration producer, which
    ///     writes its root at row 0; the MFT producer sets 5.
    /// </summary>
    public uint RootRow { get; init; }

    /// <summary>Rows the region can hold. Use <see cref="BlockLayout.ComputeSlotCapacity" />.</summary>
    public required uint SlotCapacity { get; init; }

    /// <summary>Allocatable name entries, independent of <see cref="SlotCapacity" />; use <see cref="BlockLayout.PlanCapacities" />.</summary>
    public required uint NameEntryCapacity { get; init; }

    /// <summary>Allocatable delta entries; use <see cref="BlockLayout.PlanCapacities" />.</summary>
    public required uint DeltaCapacity { get; init; }

    /// <summary>Name byte-pool capacity. Use <see cref="BlockLayout.ComputeNamePoolCapacity" /> with the slot capacity.</summary>
    public required uint NamePoolCapacity { get; init; }

    /// <summary>
    ///     No-cache mode: the file is removed when the mapping closes, so whole-volume metadata
    ///     never persists on disk.
    /// </summary>
    public bool DeleteOnClose { get; init; }

    /// <summary>
    ///     Receives the log line when a block whose creation failed is deleted. Null (the default) logs nothing. Forwarded from
    ///     <see cref="FileIndexOptions.Diagnostics" /> by the index; producers may leave it null.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>
    ///     The consumer identity to record in the header; a mismatch on a later warm-start
    ///     attempt reports <see cref="BlockValidationResult.WrongCacheTag" />. Written before the block is marked
    ///     complete.
    /// </summary>
    public CacheTag CacheTag { get; init; }

    /// <summary>
    ///     The library's identity for the options that shape the rows, from <see cref="MFTLib.Index.ScanShape" />; a
    ///     mismatch on a later warm-start attempt reports <see cref="BlockValidationResult.WrongScanShape" />.
    /// </summary>
    public ulong ScanShape { get; init; }
}
