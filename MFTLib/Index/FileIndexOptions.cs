namespace MFTLib.Index;

/// <summary>
///     How to open an index. An empty drive set opens an index with no drives, which is a valid
///     state, not an error.
/// </summary>
public sealed record FileIndexOptions
{
    public IReadOnlyList<IndexedDrive> Drives { get; init; } = [];

    /// <summary>Null resolves to <see cref="CacheDirectory.ResolveDefaultPath" />.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    ///     Creates each block in the temp directory instead of the cache directory. The file is
    ///     deleted when <see cref="FileIndex.DisposeAsync" /> releases the block that owns it
    ///     (or, for a block nothing references any more, whenever the runtime finalizes it),
    ///     not through <see cref="FileOptions.DeleteOnClose" />, which only fires when the
    ///     process's own handle closes. A process that is killed rather than disposed leaves the
    ///     temp file behind; the next <see cref="FileIndex.OpenAsync" /> for that drive removes
    ///     any such leftover it recognizes by name before scanning.
    /// </summary>
    public bool NoCache { get; init; }

    public ProducerPolicy ProducerPolicy { get; init; } = ProducerPolicy.Auto;

    public IProgress<IndexScanProgress>? Progress { get; init; }
}
