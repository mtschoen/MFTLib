namespace MFTLib.Index;

/// <summary>
///     Identifies one index row on one drive: the row key within that drive's block. For an MFT
///     block the record number is the NTFS MFT segment index, but it is not a full NTFS file
///     identifier: a file-id open also needs the sequence number, which is stored per row, and
///     NTFS reuses a segment index after a file is deleted. For an enumeration block it is the
///     row index the producer assigned in traversal order; <see cref="ProducerKind" /> says which.
/// </summary>
public readonly record struct FileId(char DriveLetter, ulong RecordNumber, ProducerKind ProducerKind)
{
    /// <summary>Formats this identifier as a drive letter and unsigned record number.</summary>
    /// <returns>A diagnostic representation of this identifier.</returns>
    public override string ToString()
    {
        return $"{DriveLetter}:{RecordNumber}";
    }
}
