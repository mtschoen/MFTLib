namespace MFTLib.Index;

/// <summary>
///     One journal batch as a volume's watch delivers it. The cursor travels with the batch rather
///     than being tracked by the index, so a watch that resumes or skips ahead after a journal
///     wrap reports where it actually is.
/// </summary>
sealed record JournalBatch(IReadOnlyList<UsnJournalEntry> Entries, ulong JournalIdentifier, long NextUsn)
    : WatchStreamItem;
