namespace MFTLib.Index;

/// <summary>
///     One progress sample from whichever producer is building a root's block. A root's samples move through
///     <see cref="IndexScanPhase" /> in order and never back within a scan; the
///     <see cref="IndexScanPhase.Finished" /> sample ends the scan, and <see cref="Outcome" /> is non-null only on it.
///     <see cref="CurrentDirectory" /> is null for an MFT scan, which has no notion of a current directory.
/// </summary>
public sealed record IndexScanProgress
{
    /// <summary>Creates one progress sample.</summary>
    /// <param name="root">The root whose block is being produced.</param>
    /// <param name="phase">The current stage of the scan.</param>
    /// <param name="rowsWritten">The phase's count, as <see cref="WrittenRowCount" /> describes it.</param>
    internal IndexScanProgress(RootKey root, IndexScanPhase phase, uint rowsWritten)
    {
        Root = root;
        Phase = phase;
        WrittenRowCount = rowsWritten;
    }

    /// <summary>Root whose block is being produced.</summary>
    public RootKey Root { get; init => field = value.Validated(); }

    /// <summary>Current stage of the scan.</summary>
    public IndexScanPhase Phase { get; init; }

    /// <summary>
    ///     The phase's count: rows written while <see cref="IndexScanPhase.Enumerating" />; records processed, which
    ///     never decreases, while <see cref="IndexScanPhase.Scanning" />; zero while
    ///     <see cref="IndexScanPhase.Indexing" />; and on <see cref="IndexScanPhase.Finished" />, the finished block's
    ///     row count on success and zero otherwise.
    /// </summary>
    public uint WrittenRowCount { get; init; }

    /// <summary>
    ///     While <see cref="IndexScanPhase.Scanning" />, the records the scan expects to process (the volume's MFT slot
    ///     count), or null when the producer does not know it. Null in every other phase: an enumeration walk never
    ///     knows its total.
    /// </summary>
    public uint? ExpectedRowCount { get; init; }

    /// <summary>Current directory during enumeration, or null for producers without directories.</summary>
    public string? CurrentDirectory { get; init; }

    /// <summary>Terminal scan outcome on a finished sample; otherwise, null.</summary>
    public IndexScanOutcome? Outcome { get; init; }
}
