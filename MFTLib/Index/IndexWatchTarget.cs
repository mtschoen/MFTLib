namespace MFTLib.Index;

/// <summary>The persisted journal cursor from which one drive's watch resumes.</summary>
public sealed record IndexWatchTarget(char DriveLetter, ulong JournalId, long NextUsn);
