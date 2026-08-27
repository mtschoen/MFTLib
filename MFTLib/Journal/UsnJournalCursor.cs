namespace MFTLib;

/// <summary>
///     Tracks position in a volume's USN journal for resumable reads.
///     Persist this between runs to enable incremental scanning.
/// </summary>
public readonly record struct UsnJournalCursor(ulong JournalId, long NextUsn);
