namespace MFTLib.Index;

/// <summary>The outcome of one canonical cached-block deletion attempt.</summary>
public enum CachedBlockDeletionOutcome
{
    /// <summary>The block file is gone: this call removed it under its ownership lock, or it was already absent.</summary>
    Deleted,
    /// <summary>The ownership lock could not be acquired because of contention or an access or I/O failure, so the file was left in place.</summary>
    InUse,
    /// <summary>The lock was acquired but the filesystem refused the delete; the reason is reported alongside.</summary>
    Failed
}
