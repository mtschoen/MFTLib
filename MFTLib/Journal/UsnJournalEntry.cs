namespace MFTLib;

readonly record struct NativeUsnJournalEntryData
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public required long Usn { get; init; }
    public required long FileTimeTimestamp { get; init; }
    public required uint Reason { get; init; }
    public required uint FileAttributes { get; init; }
    public required string FileName { get; init; }
}

public readonly struct UsnJournalEntry
{
    /// <summary>
    /// MFT segment index (48-bit, sequence number stripped). Matches MftRecord.RecordNumber.
    /// Safe to use as a dictionary key across MFT scans and USN journal reads on the same volume.
    /// </summary>
    public ulong RecordNumber { get; }

    /// <summary>
    /// Parent directory's MFT segment index (48-bit, sequence number stripped). Matches MftRecord.ParentRecordNumber.
    /// The NTFS root directory is segment 5 (its parent is also 5).
    /// </summary>
    public ulong ParentRecordNumber { get; }
    public long Usn { get; }
    public DateTime Timestamp { get; }
    public UsnReason Reason { get; }
    public FileAttributes FileAttributes { get; }
    public string FileName { get; }

    public bool IsClose => (Reason & UsnReason.Close) != 0;
    public bool IsCreate => (Reason & UsnReason.FileCreate) != 0;
    public bool IsDelete => (Reason & UsnReason.FileDelete) != 0;
    public bool IsRename => (Reason & (UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0;

    internal UsnJournalEntry(NativeUsnJournalEntryData data)
    {
        RecordNumber = data.RecordNumber;
        ParentRecordNumber = data.ParentRecordNumber;
        Usn = data.Usn;
        Timestamp = data.FileTimeTimestamp > 0
            ? DateTime.FromFileTimeUtc(data.FileTimeTimestamp)
            : DateTime.MinValue;
        Reason = (UsnReason)data.Reason;
        FileAttributes = (FileAttributes)data.FileAttributes;
        FileName = data.FileName;
    }

    UsnJournalEntry(UsnJournalEntryOptions options)
    {
        RecordNumber = options.RecordNumber;
        ParentRecordNumber = options.ParentRecordNumber;
        Usn = options.Usn;
        Timestamp = options.Timestamp;
        Reason = options.Reason;
        FileAttributes = options.FileAttributes;
        FileName = options.FileName;
    }

    /// <summary>
    /// Construct a USN journal entry from already-decoded values. For callers that
    /// produce entries outside the native marshaling path (e.g. a tool that
    /// serializes journal data to disk and reconstructs it in another process).
    /// </summary>
    public static UsnJournalEntry Create(UsnJournalEntryOptions options) => new(options);

    public override string ToString() => $"[{Reason}] {FileName} (record {RecordNumber})";
}
