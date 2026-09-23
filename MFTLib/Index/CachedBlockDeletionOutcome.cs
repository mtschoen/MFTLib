namespace MFTLib.Index;

/// <summary>The outcome of one canonical cached-block deletion attempt.</summary>
public enum CachedBlockDeletionOutcome
{
    /// <summary>Deletion completed, including an already-absent inventory entry.</summary>
    Deleted,
    /// <summary>The ownership lock was held or could not be opened.</summary>
    InUse,
    /// <summary>The file could not be deleted after its ownership lock was acquired.</summary>
    Failed
}
