namespace MFTLib.Index;

/// <summary>
///     One volume's USN change journal sizing, as reported by
///     <c>FSCTL_QUERY_USN_JOURNAL</c>. The journal is a ring buffer: when producers write
///     records faster than watchers drain them and the buffer wraps, the overwritten
///     records are gone and every consumer that missed them must rescan.
/// </summary>
public readonly record struct UsnJournalSettings
{
    /// <summary>The journal's maximum size in bytes: the ring buffer's capacity.</summary>
    public required long MaximumSize { get; init; }

    /// <summary>
    ///     The allocation delta in bytes: how much the journal file grows by when it needs
    ///     more room, and the granularity sizes round to.
    /// </summary>
    public required long AllocationDelta { get; init; }

    /// <summary>
    ///     True when <see cref="MaximumSize" /> is below
    ///     <see cref="UsnJournalRecommendations.RecommendedMaximumSize" />. Consumers surface
    ///     this as a warning with an explicit enlarge action; MFTLib never changes the
    ///     sizing by itself.
    /// </summary>
    public bool IsBelowRecommended => MaximumSize < UsnJournalRecommendations.RecommendedMaximumSize;
}
