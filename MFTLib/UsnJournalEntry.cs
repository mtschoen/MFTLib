namespace MFTLib;

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

    internal UsnJournalEntry(ulong recordNumber, ulong parentRecordNumber,
        long usn, long fileTimeTimestamp, uint reason, uint fileAttributes, string fileName)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        Usn = usn;
        Timestamp = fileTimeTimestamp > 0
            ? DateTime.FromFileTimeUtc(fileTimeTimestamp)
            : DateTime.MinValue;
        Reason = (UsnReason)reason;
        FileAttributes = (FileAttributes)fileAttributes;
        FileName = fileName;
    }

    public override string ToString() => $"[{Reason}] {FileName} (record {RecordNumber})";
}
