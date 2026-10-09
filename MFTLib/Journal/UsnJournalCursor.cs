namespace MFTLib;

/// <summary>
///     Tracks position in a volume's USN journal for resumable reads.
///     Persist this between runs to enable incremental scanning.
/// </summary>
internal readonly record struct UsnJournalCursor(ulong JournalIdentifier, long NextUsn);
