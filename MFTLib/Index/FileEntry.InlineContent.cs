namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>The sequence number of the incarnation this handle was minted for: what a record read must still carry.</summary>
    internal ushort SequenceNumber => _sequenceNumber;
}
