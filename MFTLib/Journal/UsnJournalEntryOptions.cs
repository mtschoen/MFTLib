namespace MFTLib;

/// <summary>Decoded values used to construct a <see cref="UsnJournalEntry" />.</summary>
public readonly record struct UsnJournalEntryOptions
{
    /// <summary>Lower 48-bit NTFS MFT segment index of the changed item.</summary>
    public required ulong RecordNumber { get; init; }
    /// <summary>Lower 48-bit NTFS MFT segment index of the parent directory.</summary>
    public required ulong ParentRecordNumber { get; init; }
    /// <summary>Upper 16 bits of the changed item's NTFS file reference.</summary>
    public ushort SequenceNumber { get; init; }
    /// <summary>Monotonic USN value assigned to the change.</summary>
    public required long Usn { get; init; }
    /// <summary>UTC time associated with the journal record.</summary>
    public required DateTime Timestamp { get; init; }
    /// <summary>Reasons that caused the journal record.</summary>
    public required UsnReason Reason { get; init; }
    /// <summary>NTFS file attributes captured by the record.</summary>
    public required FileAttributes FileAttributes { get; init; }
    /// <summary>Changed item name without a parent path.</summary>
    public required string FileName { get; init; }
}
