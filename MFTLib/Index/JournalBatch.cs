namespace MFTLib.Index;

/// <summary>
///     One journal batch as a drive's watch delivers it. The cursor travels with the batch rather
///     than being tracked by the index, so a watch that resumes or skips ahead after a journal
///     wrap reports where it actually is.
/// </summary>
internal sealed record JournalBatch(IReadOnlyList<UsnJournalEntry> Entries, ulong JournalId, long NextUsn)
    : WatchStreamItem;
