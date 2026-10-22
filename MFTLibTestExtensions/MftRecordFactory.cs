using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>Test-package values for constructing a materialized MFT record, including raw FILETIME.</summary>
sealed record MftRecordTestValues
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public bool InUse { get; init; } = true;
    public bool IsDirectory { get; init; }
    public bool SizeKnown { get; init; } = true;
    public required string FileName { get; init; }
    public FileAttributes FileAttributes { get; init; }
    public long Size { get; init; }
    public long ModifiedFileTime { get; init; }
    public ushort SequenceNumber { get; init; }
    public ushort ParentSequenceNumber { get; init; }
}

/// <summary>Constructs test records through MFTLib's materialized-record constructor.</summary>
static class MftRecordFactory
{
    internal static MftRecord Create(MftRecordTestValues values)
    {
        var flags = (ushort)((values.InUse ? 1 : 0)
                             | (values.IsDirectory ? 2 : 0)
                             | (values.SizeKnown ? 0 : 0x8000));
        var fields = new MftRecordFields(flags, values.FileAttributes, values.Size, values.ModifiedFileTime,
            values.SequenceNumber, values.ParentSequenceNumber);
        return new MftRecord(values.RecordNumber, values.ParentRecordNumber, fields, values.FileName);
    }
}
