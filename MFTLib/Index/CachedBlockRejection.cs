namespace MFTLib.Index;

/// <summary>A directory entry excluded from the cache inventory because its filename is not canonical.</summary>
public sealed record CachedBlockRejection
{
    /// <summary>Creates a rejection.</summary>
    /// <param name="path">The full path to the rejected file.</param>
    /// <param name="reason">A human-readable explanation of the filename rejection.</param>
    internal CachedBlockRejection(string path, string reason)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(reason);
        Path = path;
        Reason = reason;
    }

    /// <summary>The full path to the rejected file. Its contents have not been opened.</summary>
    public string Path { get; init; }

    /// <summary>A human-readable explanation of the filename rejection.</summary>
    public string Reason { get; init; }
}
