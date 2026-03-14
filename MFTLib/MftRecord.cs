namespace MFTLib;

public readonly struct MftRecord
{
    public ulong RecordNumber { get; }
    public ulong ParentRecordNumber { get; }
    public bool InUse { get; }
    public bool IsDirectory { get; }
    public string FileName { get; }
    public string? FullPath { get; }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, ushort flags, string fileName, string? fullPath = null)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        InUse = (flags & 1) != 0;
        IsDirectory = (flags & 2) != 0;
        FileName = fileName;
        FullPath = fullPath;
    }

    public override string ToString() => FullPath ?? FileName;
}
