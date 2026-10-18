namespace MFTLib;

/// <summary>
///     Configures freed-record inclusion for a cold MFT scan, broker-backed or in-process. Row retention
///     is <see cref="MFTLib.Index.FileIndexOptions.RetainedFileNames" />. Scan progress arrives through
///     <see cref="MFTLib.Index.FileIndexOptions.ScanProgress" />.
/// </summary>
public sealed record MftScanOptions
{
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
}
