namespace MFTLib;

/// <summary>The stage a block production reports; a scan never returns to an earlier one.</summary>
enum BlockScanPhase : byte
{
    /// <summary>Records are read, parsed and written as rows.</summary>
    Scanning = 0,

    /// <summary>Every row is written; the postings are built and the block is flushed.</summary>
    Indexing = 1
}

/// <summary>
///     One progress sample of a block production. While <see cref="BlockScanPhase.Scanning" />, the records the parse
///     has processed, which never decreases, out of the slot count it expects; <see cref="BlockScanPhase.Indexing" />
///     carries no count.
/// </summary>
readonly record struct BlockWriteProgress(BlockScanPhase Phase, long RecordsProcessed, long? TotalRecords)
{
    /// <summary>
    ///     Reported once per production, after the last row and before the posting build, and never by a production
    ///     whose name regions ran out, since its producer discards the block and scans again.
    /// </summary>
    internal static BlockWriteProgress Indexing { get; } = new(BlockScanPhase.Indexing, 0, null);

    /// <summary>A native parse's progress: the records it has processed, which never decreases, out of the slot count.</summary>
    internal static BlockWriteProgress Scanning(long recordsProcessed, long? totalRecords) =>
        new(BlockScanPhase.Scanning, recordsProcessed, totalRecords);
}
