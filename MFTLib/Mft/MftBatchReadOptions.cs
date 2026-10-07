namespace MFTLib;

/// <summary>What a batched volume read does beyond the parse itself.</summary>
internal readonly record struct MftBatchReadOptions
{
    /// <summary>Whether the parse also returns records NTFS has freed.</summary>
    public bool IncludeFreed { get; init; }

    /// <summary>Told how many allocated records were passed over for an invalid fixup, before the first batch.</summary>
    public Action<ulong>? UnreadableRecords { get; init; }
}
