namespace MFTLib.Index;

/// <summary>
///     The stage of one root scan, in order. A root's samples never move back to an earlier phase within a scan; a
///     phase can be skipped (an enumeration walk is never <see cref="Scanning" />, and a failed scan can end before
///     <see cref="Indexing" />).
/// </summary>
public enum IndexScanPhase
{
    /// <summary>
    ///     Emitted by the enumeration producer while walking managed filesystem directories, with the rows written so
    ///     far in <see cref="IndexScanProgress.WrittenRowCount" />.
    /// </summary>
    Enumerating,

    /// <summary>
    ///     Emitted by an MFT producer, in process or through the broker, while it reads, parses and writes the volume's
    ///     records. <see cref="IndexScanProgress.WrittenRowCount" /> is the records processed, which never decreases,
    ///     out of <see cref="IndexScanProgress.ExpectedRowCount" />, the volume's MFT slot count.
    /// </summary>
    Scanning,

    /// <summary>
    ///     Emitted once every row is written, while the block's name postings are built and the block is flushed. It
    ///     carries no count.
    /// </summary>
    Indexing,

    /// <summary>
    ///     Emitted by <see cref="FileIndex" /> exactly once per root scan, after the producer's
    ///     last sample for that root, whether the scan succeeded, failed or was cancelled
    ///     (<see cref="IndexScanProgress.Outcome" /> says which). Every producer, enumeration
    ///     or MFT-backed, is covered. A rescan, and a retry after a lost catch-up, are each a
    ///     scan of their own and each report one, so track finished roots as a set.
    /// </summary>
    Finished
}
