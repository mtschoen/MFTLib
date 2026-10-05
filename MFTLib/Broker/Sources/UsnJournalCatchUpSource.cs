namespace MFTLib;

/// <summary>
///     Production wires this to a bounded, non-watching read of the USN journal: at most
///     <paramref name="maximumBufferReads" /> journal buffers recorded since <paramref name="since" />
///     (0 reads to the journal tip), with the cursor the next call resumes from. A call that
///     returns its <paramref name="since" /> cursor unchanged has reached the tip and returns no entries. Tests inject a
///     fake so <see cref="JournalBrokerHost" /> can be exercised without a real elevated volume handle.
/// </summary>
internal delegate (UsnJournalEntry[] Entries, UsnJournalCursor Updated) UsnJournalCatchUpSource(
    string driveLetter,
    UsnJournalCursor since,
    int maximumBufferReads);
