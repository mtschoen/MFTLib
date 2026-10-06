namespace MFTLib.Index;

/// <summary>
///     Keys one index row on one drive: the row number within that drive's block. It is a row
///     key, not a file identity. For an MFT block the record number is the NTFS MFT segment
///     index, but it is not sufficient for an open by file id, which also needs the sequence
///     number stored per row, and NTFS reuses a segment index after a file is deleted, so pair
///     it with the sequence number to identify a file. For an enumeration block it is the
///     row index the producer assigned in traversal order; <see cref="ProducerKind" /> says which.
/// </summary>
public readonly record struct IndexRecordKey(char DriveLetter, ulong RecordNumber, ProducerKind ProducerKind);
