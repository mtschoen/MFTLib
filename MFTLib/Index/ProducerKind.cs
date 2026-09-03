namespace MFTLib.Index;

/// <summary>
///     Which producer wrote a block. Stored in the block header so a reader can tell an
///     MFT-derived block (record numbers are real NTFS segment indexes) from an
///     enumeration-derived block (row indexes are assigned sequentially in traversal order).
/// </summary>
public enum ProducerKind : uint
{
    Mft = 1,
    Enumeration = 2
}
