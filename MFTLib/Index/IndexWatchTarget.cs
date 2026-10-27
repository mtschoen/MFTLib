namespace MFTLib.Index;

/// <summary>The persisted journal cursor from which one volume's watch resumes.</summary>
sealed record IndexWatchTarget(VolumeKey Volume, ulong JournalIdentifier, long NextUsn);
