namespace MFTLib.Index;

/// <summary>
///     Which producer wrote a block. Stored in the block header so a reader can tell an
///     MFT-derived block (record numbers are real NTFS segment indexes) from an
///     enumeration-derived block (row indexes are assigned sequentially in traversal order).
/// </summary>
public enum ProducerKind : uint
{
    /// <summary>Row numbers are NTFS MFT segment indexes; a segment index may be reused by a later file, so it is not a stable file identity by itself.</summary>
    Mft = 1,
    /// <summary>Row numbers were assigned in traversal order and do not correspond to NTFS segment indexes.</summary>
    Enumeration = 2
}
