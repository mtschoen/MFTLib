namespace MFTLibTestExtensions;

/// <summary>Every column of a synthetic materialized MFT record. Only the identity of the record is required.</summary>
public sealed record SyntheticMftRecordOptions
{
    /// <summary>The record's MFT segment index.</summary>
    public required ulong RecordNumber { get; init; }

    /// <summary>The parent directory's MFT segment index.</summary>
    public required ulong ParentRecordNumber { get; init; }

    /// <summary>The record's own name, never a path; the path belongs to <see cref="FullPath" />.</summary>
    public required string FileName { get; init; }

    /// <summary>The resolved full path, or null when the record carries none.</summary>
    public string? FullPath { get; init; }

    /// <summary>Whether the record header marks a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Whether the record is allocated. Defaults to true.</summary>
    public bool InUse { get; init; } = true;

    /// <summary>Whether the data size is known. Defaults to true; false sets the size-unknown flag.</summary>
    public bool SizeKnown { get; init; } = true;

    /// <summary>The Win32 file attributes. Null derives <see cref="FileAttributes.Directory" /> or <see cref="FileAttributes.Normal" /> from <see cref="IsDirectory" />.</summary>
    public FileAttributes? FileAttributes { get; init; }

    /// <summary>The size of the unnamed data stream in bytes; ignored by readers when <see cref="SizeKnown" /> is false.</summary>
    public long Size { get; init; }

    /// <summary>The last write time in UTC. Null means the Unix epoch.</summary>
    public DateTime? ModifiedUtc { get; init; }

    /// <summary>The record's sequence number.</summary>
    public ushort SequenceNumber { get; init; }
}
