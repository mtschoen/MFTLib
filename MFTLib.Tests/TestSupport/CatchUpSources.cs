using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>Fake catch-up sources that honor the bounded-read contract of <see cref="IVolumeAccess.ReadJournal" />.</summary>
internal static class CatchUpSources
{
    /// <summary>
    ///     Returns <paramref name="entries" /> and advances to <paramref name="tip" /> from any other
    ///     cursor; from the tip itself it returns nothing and the tip unchanged, which ends catch-up.
    /// </summary>
    public static Func<DriveKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)> ToTip(
        UsnJournalCursor tip, params UsnJournalEntry[] entries)
    {
        return (_, since, _) => since == tip ? ([], since) : (entries, tip);
    }
}
