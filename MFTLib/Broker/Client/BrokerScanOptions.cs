namespace MFTLib;

/// <summary>Configures row filtering and progress for a cold scan.</summary>
public sealed record BrokerScanOptions
{
    /// <summary>
    ///     Null retains all eligible records. An empty collection retains directories only;
    ///     a nonempty collection also retains files whose names match ordinally, ignoring case.
    ///     Freed records remain controlled independently by <see cref="IncludeFreed" />.
    /// </summary>
    public IReadOnlyCollection<string>? DirectoryScanFileNames { get; init; }
    /// <summary>
    ///     When true, the scan also imports MFT records that NTFS has freed: each becomes a row
    ///     with <see cref="MFTLib.Index.FileEntry.IsDeleted" /> true that exists only for this scan, and a later
    ///     watch applies journal events to live rows only. A freed row whose parent chain still
    ///     verifies keeps its parent and full path; any other freed row is detached, so its path is
    ///     its bare name and it is nobody's child. The default is false. A cached block carries the
    ///     rows of the scan that produced it, so a consumer that changes this option also changes its
    ///     <see cref="MFTLib.Index.CacheTag" /> version.
    /// </summary>
    public bool IncludeFreed { get; init; }
    /// <summary>
    ///     Receives progress reported while the broker performs the cold scan, or null when
    ///     no progress notifications are required.
    /// </summary>
    internal IProgress<BrokerScanProgress>? Progress { get; init; }
}
