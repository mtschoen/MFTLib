namespace MFTLib.Index;

/// <summary>
///     One progress sample from whichever producer is building a root's block.
///     <see cref="ExpectedRowCount" /> is null while the producer does not yet know the total, which
///     an enumeration walk never does. <see cref="CurrentDirectory" /> is null for an MFT scan,
///     which has no notion of a current directory. The <see cref="IndexScanPhase.Finished" />
///     sample ends a root's scan: <see cref="Outcome" /> is non-null only on it, and
///     <see cref="WrittenRowCount" /> is then the finished block's row count on success and 0 otherwise.
/// </summary>
public sealed record IndexScanProgress
{
    /// <summary>Creates one progress sample.</summary>
    /// <param name="root">The root whose block is being produced.</param>
    /// <param name="phase">The current stage of the scan.</param>
    /// <param name="rowsWritten">
    ///     Records scanned during parsing, rows transferred during intermediate transfer samples, or rows written during enumeration.
    ///     The final broker transfer sample uses the known parsing total, otherwise the block row count, raised to at least the maximum parsing count.
    ///     Finished samples carry the final block row count on success, otherwise zero.
    /// </param>
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
    ///     Records scanned during parsing, rows transferred during intermediate transfer samples, or rows written during enumeration.
    ///     The final broker transfer sample uses the known parsing total, otherwise the block row count, raised to at least the maximum parsing count.
    ///     A successful finished sample carries the final block row count; other finished samples carry zero.
    /// </summary>
    public uint WrittenRowCount { get; init; }

    /// <summary>
    ///     Expected total scanned records during parsing or transferred rows during intermediate transfer samples, when known; otherwise, null.
    ///     The final broker transfer sample uses the known parsing total, otherwise the block row count, raised to at least the maximum parsing count.
    /// </summary>
    public uint? ExpectedRowCount { get; init; }

    /// <summary>Current directory during enumeration, or null for producers without directories.</summary>
    public string? CurrentDirectory { get; init; }

    /// <summary>Terminal scan outcome on a finished sample; otherwise, null.</summary>
    public IndexScanOutcome? Outcome { get; init; }
}
