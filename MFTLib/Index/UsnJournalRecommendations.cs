namespace MFTLib.Index;

/// <summary>
///     The USN change journal sizing MFTLib recommends for watched volumes. Windows creates a
///     journal with a 32 MB maximum (about 200,000 change records), which a busy volume wraps
///     in minutes: a wrapped journal faults that drive's live watch and forces a cold rescan
///     at the next warm start. 128 MB is the maximum Everything (voidtools) recommends on
///     Windows 10 and later, with a 16 MB allocation delta so the journal grows in 16 MB
///     steps instead of the 32 MB default. Consumers compare a volume's
///     <see cref="UsnJournalSettings" /> against these and never hard-code the numbers:
///     MFTLib reports and warns, and changes the sizing only on an explicit consumer call.
/// </summary>
public static class UsnJournalRecommendations
{
    /// <summary>Recommended journal maximum size in bytes: 128 MB.</summary>
    public const long RecommendedMaximumSize = 128L * 1024 * 1024;

    /// <summary>Recommended journal allocation delta in bytes: 16 MB.</summary>
    public const long RecommendedAllocationDelta = 16L * 1024 * 1024;
}
