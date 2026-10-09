namespace MFTLib.Index;

/// <summary>The persisted journal cursor from which one drive's watch resumes.</summary>
internal sealed record IndexWatchTarget(char DriveLetter, ulong JournalIdentifier, long NextUsn);
