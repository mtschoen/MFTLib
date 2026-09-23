namespace MFTLib.Index;

/// <summary>A directory entry excluded from the cache inventory because its filename is not canonical.</summary>
/// <param name="Path">The full path to the rejected file. Its contents have not been opened.</param>
/// <param name="Reason">A human-readable explanation of the filename rejection.</param>
public sealed record CachedBlockRejection(string Path, string Reason);
