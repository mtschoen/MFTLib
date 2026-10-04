namespace MFTLib;

readonly record struct NativeUsnJournalEntryData
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public required ushort SequenceNumber { get; init; }
    public required long Usn { get; init; }
    public required long FileTimeTimestamp { get; init; }
    public required uint Reason { get; init; }
    public required uint FileAttributes { get; init; }
    public required string FileName { get; init; }
}

/// <summary>One decoded NTFS USN change-journal record.</summary>
public readonly struct UsnJournalEntry
{
    /// <summary>
    ///     MFT segment index (the lower 48 bits of the file reference number). Matches MftRecord.RecordNumber.
    ///     The sequence number is carried separately in <see cref="SequenceNumber" />.
    ///     Safe to use as a dictionary key across MFT scans and USN journal reads on the same volume.
    /// </summary>
    public ulong RecordNumber { get; }

    /// <summary>The upper 16 bits of the file reference, identifying reuse of an MFT segment.</summary>
    public ushort SequenceNumber { get; }

    /// <summary>
    ///     Parent directory's MFT segment index (the lower 48 bits of the file reference number).
    ///     Matches MftRecord.ParentRecordNumber. The NTFS root directory is segment 5 (its parent
    ///     is also 5).
    /// </summary>
    public ulong ParentRecordNumber { get; }

    /// <summary>Monotonic journal sequence number assigned to this change.</summary>
    public long Usn { get; }
    /// <summary>UTC timestamp of the record, or <see cref="DateTime.MinValue" /> when unavailable.</summary>
    public DateTime Timestamp { get; }
    /// <summary>NTFS reasons that caused this journal record.</summary>
    public UsnReason Reason { get; }
    /// <summary>NTFS file attributes captured with the change.</summary>
    public FileAttributes FileAttributes { get; }
    /// <summary>Name of the changed item, without a parent path.</summary>
    public string FileName { get; }

    /// <summary>Determines whether this record closes a file-handle operation.</summary>
    public bool IsClose => (Reason & UsnReason.Close) != 0;
    /// <summary>Determines whether this record reports creation.</summary>
    public bool IsCreate => (Reason & UsnReason.FileCreate) != 0;
    /// <summary>Determines whether this record reports deletion.</summary>
    public bool IsDelete => (Reason & UsnReason.FileDelete) != 0;
    /// <summary>Determines whether this record reports either half of a rename operation.</summary>
    public bool IsRename => (Reason & (UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0;

    internal UsnJournalEntry(NativeUsnJournalEntryData data)
    {
        RecordNumber = data.RecordNumber;
        ParentRecordNumber = data.ParentRecordNumber;
        SequenceNumber = data.SequenceNumber;
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
        SequenceNumber = options.SequenceNumber;
        Usn = options.Usn;
        Timestamp = options.Timestamp;
        Reason = options.Reason;
        FileAttributes = options.FileAttributes;
        FileName = options.FileName;
    }

    /// <summary>
    ///     Construct a USN journal entry from already-decoded values. For callers that
    ///     produce entries outside the native marshaling path (e.g. a tool that
    ///     serializes journal data to disk and reconstructs it in another process).
    /// </summary>
    /// <param name="options">Already-decoded journal values to copy into the entry.</param>
    /// <returns>A journal entry containing the supplied values.</returns>
    public static UsnJournalEntry Create(UsnJournalEntryOptions options)
    {
        return new UsnJournalEntry(options);
    }

    /// <summary>Formats the journal reasons, name, and record number for diagnostics.</summary>
    /// <returns>A diagnostic representation of this entry.</returns>
    public override string ToString()
    {
        return $"[{Reason}] {FileName} (record {RecordNumber})";
    }
}
