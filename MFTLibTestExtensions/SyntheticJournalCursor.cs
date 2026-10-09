namespace MFTLibTestExtensions;

/// <summary>A journal position a test scripts: which journal, and the next update sequence number to read.</summary>
/// <param name="JournalIdentifier">The identifier of the journal instance.</param>
/// <param name="NextUsn">The next position a reader resumes from.</param>
public readonly record struct SyntheticJournalCursor(ulong JournalIdentifier, long NextUsn);
