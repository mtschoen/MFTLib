namespace MFTLib;

/// <summary>
/// Production wires this to a bounded, non-watching read of the USN journal
/// (drain everything recorded since <paramref name="since"/> and return the
/// advanced cursor); tests inject a fake so <see cref="JournalBrokerHost"/>
/// can be exercised without a real elevated volume handle.
/// </summary>
public delegate (UsnJournalEntry[] Entries, UsnJournalCursor Updated) UsnJournalCatchUpSource(
    string driveLetter,
    UsnJournalCursor since);
