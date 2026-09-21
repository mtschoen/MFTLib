namespace MFTLib.Index;

/// <summary>
///     One volume's USN change journal sizing, as reported by
///     <c>FSCTL_QUERY_USN_JOURNAL</c>. The journal is a ring buffer: when producers write
///     records faster than watchers drain them and the buffer wraps, the overwritten
///     records are gone and every consumer that missed them must rescan.
/// </summary>
public readonly record struct UsnJournalSettings
{
    /// <summary>The journal's configured maximum size in bytes, used by Windows as a trimming target.</summary>
    public required long MaximumSize { get; init; }

    /// <summary>
    ///     The journal's configured allocation delta in bytes.
    /// </summary>
    public required long AllocationDelta { get; init; }
}
