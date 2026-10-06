namespace MFTLibTestExtensions;

/// <summary>One journal change a test scripts. Only the identity of the change is required.</summary>
public sealed record SyntheticJournalRecord
{
    /// <summary>The changed item's MFT segment index.</summary>
    public required ulong RecordNumber { get; init; }

    /// <summary>The parent directory's MFT segment index.</summary>
    public required ulong ParentRecordNumber { get; init; }

    /// <summary>The journal position of this record; later changes carry larger values.</summary>
    public required long UpdateSequenceNumber { get; init; }

    /// <summary>The changed item's own name, never a path.</summary>
    public required string FileName { get; init; }

    /// <summary>The reasons the record carries. Defaults to <see cref="SyntheticJournalReason.Close" />.</summary>
    public SyntheticJournalReason Reason { get; init; } = SyntheticJournalReason.Close;

    /// <summary>The file attributes the record captured. Defaults to <see cref="FileAttributes.Normal" />.</summary>
    public FileAttributes FileAttributes { get; init; } = FileAttributes.Normal;

    /// <summary>The record's UTC time. Null means the Unix epoch.</summary>
    public DateTime? Timestamp { get; init; }

    /// <summary>The upper 16 bits of the item's file reference.</summary>
    public ushort SequenceNumber { get; init; }
}
