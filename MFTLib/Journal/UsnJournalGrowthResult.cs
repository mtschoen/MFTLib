namespace MFTLib;

/// <summary>
///     Result of a <see cref="BrokerSession.GrowUsnJournalAsync" /> request, carrying the classified
///     outcome, the caller's requested maximum, the volume's maximum recommended size at request time,
///     and the journal sizing Windows reports afterwards.
/// </summary>
public readonly record struct UsnJournalGrowthResult
{
    /// <summary>Classified outcome of the growth request.</summary>
    public required UsnJournalGrowthOutcome Outcome { get; init; }

    /// <summary>Maximum journal size in bytes that the caller requested before any clamping.</summary>
    public required long RequestedMaximumSize { get; init; }

    /// <summary>
    ///     Maximum recommended journal size in bytes under MFTLib's default policy for the volume
    ///     at request time (the smaller of 8 GiB and 5 percent of the volume's total size, rounded
    ///     down to a multiple of 1 MiB).
    /// </summary>
    public required long MaximumRecommendedSize { get; init; }

    /// <summary>Live USN journal sizing reported by Windows after the operation.</summary>
    public required UsnJournalSettings JournalSettings { get; init; }
}
