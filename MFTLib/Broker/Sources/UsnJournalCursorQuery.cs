namespace MFTLib;

/// <summary>
/// Production wires this to <c>MftVolume.QueryUsnJournal</c> (arm/read the
/// current journal cursor for a drive); tests inject a fake so
/// <see cref="JournalBrokerHost"/> can be exercised without a real elevated
/// volume handle.
/// </summary>
public delegate UsnJournalCursor UsnJournalCursorQuery(string driveLetter);
