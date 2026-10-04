namespace MFTLib.Index;

/// <summary>
///     Identifies one record on one drive. For an MFT block the record number is the real NTFS
///     segment index and can be handed to a file-id open; for an enumeration block it is the
///     row index the producer assigned in traversal order, and <see cref="ProducerKind" /> says which.
/// </summary>
public readonly record struct FileId(char DriveLetter, ulong RecordNumber, ProducerKind ProducerKind)
{
    public override string ToString()
    {
        return $"{DriveLetter}:{RecordNumber}";
    }
}
