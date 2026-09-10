namespace MFTLib.Index;

/// <summary>
///     One drive's journal batch as a watch source delivers it. The cursor travels with the
///     batch rather than being tracked by the index, so a source that resumes, reconnects, or
///     skips ahead after a journal wrap reports where it actually is.
/// </summary>
public sealed record JournalBatch(char DriveLetter, IReadOnlyList<UsnJournalEntry> Entries,
    ulong JournalId, long NextUsn) : WatchStreamItem;
