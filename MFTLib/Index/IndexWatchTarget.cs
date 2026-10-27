namespace MFTLib.Index;

/// <summary>The persisted journal cursor from which one drive's watch resumes.</summary>
sealed record IndexWatchTarget(VolumeKey Drive, ulong JournalIdentifier, long NextUsn);
