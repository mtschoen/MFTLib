namespace MFTLib.Index;

/// <summary>
///     One journal batch as a volume's watch delivers it. The cursor travels with the batch rather
///     than being tracked by the index, so a watch that resumes or skips ahead after a journal
///     wrap reports where it actually is. <see cref="FileNames" /> holds the source's re-read of
///     each file the batch reports a hard-link change for, one answer per file.
/// </summary>
sealed record JournalBatch(
    IReadOnlyList<UsnJournalEntry> Entries,
    ulong JournalIdentifier,
    long NextUsn,
    IReadOnlyList<FileNameResolution> FileNames)
    : WatchStreamItem;
