namespace MFTLib;

/// <summary>
///     Classified outcome of a <see cref="BrokerSession.GrowUsnJournalAsync" /> request.
/// </summary>
public enum UsnJournalGrowthOutcome : byte
{
    /// <summary>
    ///     The requested maximum size was within the volume's default cap and exceeded the live
    ///     journal maximum, so the journal grew to the requested target.
    /// </summary>
    GrewAsRequested = 0,

    /// <summary>
    ///     The requested maximum size exceeded the volume's maximum recommended size without an override
    ///     and the live journal maximum was below that cap, so the request was clamped to the recommended
    ///     cap and grew the journal to <see cref="UsnJournalGrowthResult.MaximumRecommendedSize" />.
    /// </summary>
    GrewClampedToMaximumRecommended = 1,

    /// <summary>
    ///     The requested maximum size exceeded the volume's maximum recommended size with an explicit caller
    ///     override and exceeded the live journal maximum, so the journal grew to the full requested
    ///     target above <see cref="UsnJournalGrowthResult.MaximumRecommendedSize" />.
    /// </summary>
    GrewAboveMaximumRecommendedByOverride = 2,

    /// <summary>
    ///     The live journal's <see cref="UsnJournalSettings.MaximumSize" /> was already at or above
    ///     the effective target (the requested maximum, or the volume's cap when clamped without an
    ///     override), so no control request was sent to Windows and the journal was left unchanged.
    /// </summary>
    AlreadyAtOrAboveTarget = 3,

    /// <summary>
    ///     Windows accepted <c>FSCTL_CREATE_USN_JOURNAL</c>, but the post-call read-back
    ///     <see cref="UsnJournalSettings.MaximumSize" /> was not greater than the pre-call
    ///     maximum; <see cref="UsnJournalGrowthResult.JournalSettings" /> holds what the volume kept.
    /// </summary>
    Unchanged = 4
}
