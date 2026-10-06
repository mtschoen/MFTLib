namespace MFTLibTestExtensions;

/// <summary>
///     One record a scripted drive scan yields. The scan feeds these through the production row writer, so the
///     path of an entry comes from the parent column and not from the record. Only the identity and name are
///     public; the richer columns stay internal for library tests.
/// </summary>
public sealed record SyntheticScanRecord
{
    /// <summary>The record's MFT segment index.</summary>
    public required ulong RecordNumber { get; init; }

    /// <summary>The parent directory's MFT segment index.</summary>
    public required ulong ParentRecordNumber { get; init; }

    /// <summary>The record's own name, never a path.</summary>
    public required string FileName { get; init; }

    /// <summary>Whether the record header marks a directory.</summary>
    public bool IsDirectory { get; init; }

    internal bool InUse { get; init; } = true;

    internal FileAttributes? FileAttributes { get; init; }

    internal long Size { get; init; }

    internal bool IsSizeKnown { get; init; } = true;

    internal DateTime? LastWriteTime { get; init; }

    internal ushort SequenceNumber { get; init; }
}
