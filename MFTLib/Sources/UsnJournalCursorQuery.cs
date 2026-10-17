namespace MFTLib;

/// <summary>
///     Production wires this to <c>UsnJournalVolume.QueryUsnJournalCursor</c> (arm/read the
///     current journal cursor for a drive); tests inject a fake so
///     a host or direct producer can be exercised without a real elevated
///     volume handle.
/// </summary>
internal delegate UsnJournalCursor UsnJournalCursorQuery(string driveLetter);
