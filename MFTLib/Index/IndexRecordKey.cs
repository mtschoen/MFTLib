namespace MFTLib.Index;

/// <summary>
///     Keys one index row on one drive: the row number within that drive's block. It is a row
///     key, not a file identity. For an MFT block the record number is the NTFS MFT segment
///     index, but it is not sufficient for an open by file id, which also needs the sequence
///     number stored per row, and NTFS reuses a segment index after a file is deleted, so pair
///     it with the sequence number to identify a file. A <see cref="FileEntry" /> does that pairing
///     itself: it binds to the sequence number its row held when it was minted. For an enumeration block it is the
///     row index the producer assigned in traversal order; <see cref="ProducerKind" /> says which.
/// </summary>
public readonly record struct IndexRecordKey(RootKey Drive, ulong RecordNumber, ProducerKind ProducerKind)
{
    /// <summary>Identifies the row's volume independently of its record number; default keys are rejected.</summary>
    public RootKey Drive { get; init => field = value.Validated(); } = Drive.Validated();
}
