using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>Every column of a synthetic journal entry. Only the identity of the change is required.</summary>
public sealed record SyntheticJournalEntryOptions
{
    /// <summary>The changed item's MFT segment index.</summary>
    public required ulong RecordNumber { get; init; }

    /// <summary>The parent directory's MFT segment index.</summary>
    public required ulong ParentRecordNumber { get; init; }

    /// <summary>The journal position of this entry; later changes carry larger values.</summary>
    public required long Usn { get; init; }

    /// <summary>The changed item's own name, never a path.</summary>
    public required string FileName { get; init; }

    /// <summary>The reasons the entry records. Defaults to <see cref="UsnReason.Close" />.</summary>
    public UsnReason Reason { get; init; } = UsnReason.Close;

    /// <summary>The file attributes the entry captured. Defaults to <see cref="FileAttributes.Normal" />.</summary>
    public FileAttributes FileAttributes { get; init; } = FileAttributes.Normal;

    /// <summary>The entry's UTC time. Null means the Unix epoch.</summary>
    public DateTime? TimestampUtc { get; init; }

    /// <summary>The upper 16 bits of the item's file reference.</summary>
    public ushort SequenceNumber { get; init; }
}
