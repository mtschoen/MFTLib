namespace MFTLib;

/// <summary>Decoded values used to construct a <see cref="UsnJournalEntry" />.</summary>
public readonly record struct UsnJournalEntryOptions
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public required long Usn { get; init; }
    public required DateTime Timestamp { get; init; }
    public required UsnReason Reason { get; init; }
    public required FileAttributes FileAttributes { get; init; }
    public required string FileName { get; init; }
}
