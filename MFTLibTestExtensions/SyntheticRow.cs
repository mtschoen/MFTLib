namespace MFTLibTestExtensions;

/// <summary>
///     One row of a synthetic block: the description the real block writer consumes. A
///     <see cref="Size" /> of null writes the size-unknown flag, the way an enumeration
///     producer records a file it could not stat.
/// </summary>
/// <param name="Row">Row number, which is the NTFS record number for an MFT block.</param>
/// <param name="Name">The file or directory name.</param>
/// <param name="ParentRow">Row number of the parent directory. The root row points at itself.</param>
public sealed record SyntheticRow(uint Row, string Name, uint ParentRow)
{
    /// <summary>True for a directory. A directory's size is zero.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>True for a row whose record was deleted; the name is kept so a change feed can still name it.</summary>
    public bool IsTombstone { get; init; }

    /// <summary>
    ///     True for a slot that holds no record: the in-use flag is clear, as for a slot never filled or
    ///     freed, so scans, lookups and counts skip it. The other columns are still stored, which lets a
    ///     test seed stale data a reader must ignore. <see cref="SyntheticBlock.ReadRows" /> reports only rows in use.
    /// </summary>
    public bool IsFree { get; init; }

    /// <summary>Stored as the raw attribute word, so any combination a search or filter inspects can be seeded.</summary>
    public FileAttributes Attributes { get; init; }

    /// <summary>The size in bytes, or null when the producer could not determine it.</summary>
    public long? Size { get; init; } = 0;

    /// <summary>The last write time. Only its UTC ticks are stored.</summary>
    public DateTime ModifiedUtc { get; init; }

    /// <summary>The NTFS record sequence number, or zero.</summary>
    public ushort SequenceNumber { get; init; }
}
