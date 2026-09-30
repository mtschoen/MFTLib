namespace MFTLib.Index;

/// <summary>The outcome of one canonical cached-block deletion attempt.</summary>
public enum CachedBlockDeletionOutcome
{
    /// <summary>The block file is gone: this call removed it under its ownership lock, or it was already absent.</summary>
    Deleted,
    /// <summary>Another live index or process owns the block, so its lock was held or could not be opened and the file was left in place.</summary>
    InUse,
    /// <summary>The lock was acquired but the filesystem refused the delete; the reason is reported alongside.</summary>
    Failed
}
