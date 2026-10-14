namespace MFTLib.Index;

/// <summary>
///     How to open an index. For ordinary sources, an empty drive set opens an index with no
///     drives; a dump source supplies its own virtual drive and requires an empty drive set.
/// </summary>
public sealed record FileIndexOptions
{
    /// <summary>
    ///     Drives to include in the index. For ordinary sources, an empty collection opens an
    ///     index with no drives; a dump source supplies its own virtual drive and requires an
    ///     empty collection.
    /// </summary>
    public IReadOnlyList<IndexedDrive> Drives { get; init; } = [];

    /// <summary>
    ///     How the open uses the block cache. Null (the default) lets the library pick what fits the
    ///     source: <see cref="MFTLib.Index.CacheMode.Disabled" /> for a dump source and
    ///     <see cref="MFTLib.Index.CacheMode.Persistent" /> otherwise. A dump source rejects every explicit
    ///     value, <see cref="MFTLib.Index.CacheMode.Disabled" /> included.
    /// </summary>
    public CacheMode? CacheMode { get; init; }

    /// <summary>
    ///     An application-owned cache directory. Required and nonblank for
    ///     <see cref="MFTLib.Index.CacheMode.Persistent" /> and <see cref="MFTLib.Index.CacheMode.ExistingOnly" />,
    ///     including empty-drive opens. Missing configuration causes
    ///     <see cref="FileIndex.OpenAsync" /> to throw <see cref="ArgumentException" />.
    ///     Ignored, never resolved or created, with <see cref="MFTLib.Index.CacheMode.Disabled" />; dump sources
    ///     prohibit this option.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    ///     Supplies MFT-derived blocks and each drive's live watch from one place. Null is a
    ///     configuration error for a drive whose <see cref="IndexedDrive.ProducerPolicy" /> is
    ///     <see cref="ProducerPolicy.Mft" /> when a scan or watch is needed. A missing source fails
    ///     that drive's scan with <see cref="DriveFailureKind.ProducerFailed" />; enumeration drives
    ///     ignore it, and warm MFT drives need none until a scan or watch is requested. A rescan of a watched drive
    ///     stops that drive's watch when the replacement block commits and starts a fresh one from the new
    ///     block's cursor through the same source; a rescan whose production fails or is cancelled leaves a
    ///     healthy watch running.
    /// </summary>
    public MftIndexSource? MftSource { get; init; }

    /// <summary>
    ///     Samples from whichever producer is building a drive's block. Each drive scan, in
    ///     <see cref="FileIndex.OpenAsync" /> and in a rescan alike, ends with exactly one
    ///     <see cref="IndexScanPhase.Finished" /> sample whose <see cref="IndexScanProgress.Outcome" />
    ///     says whether it succeeded, failed or was cancelled. Drives scan concurrently, so
    ///     samples of different drives interleave. A handler that throws on the Finished sample is
    ///     contained and reported to <see cref="Diagnostics" />, so it never replaces the scan's own
    ///     exception or result. A throw on any other sample fails that scan like any producer fault,
    ///     except a <see cref="IndexScanPhase.ParsingMft" /> sample of an in-process scan, local-volume
    ///     or dump alike: the native parse callback that carries it catches every exception from
    ///     reporting, drops it and continues the scan. Broker-delivered samples and the managed
    ///     transfer phase carry no such catch, so a throw from either fails the scan. Marshalling
    ///     belongs to the <see cref="IProgress{T}" /> implementation.
    /// </summary>
    public IProgress<IndexScanProgress>? Progress { get; init; }

    /// <summary>
    ///     Open-time per-drive progress: <see cref="FileIndex.OpenAsync" /> reports one
    ///     <see cref="IndexDriveOpened" /> for each drive that settles, synchronously on the thread
    ///     that settled it, whatever the outcome: warm-started, cold-scanned, declined by
    ///     <see cref="MFTLib.Index.CacheMode.ExistingOnly" />, offline, or failed. A drive whose settle is
    ///     cancelled does not settle and reports nothing, so a cancelled or failed open may have
    ///     reported only some of the configured drives. Drives settle concurrently and no lock is held while a handler runs,
    ///     so reports can overlap and can arrive out of order; a handler that blocks holds up no
    ///     other drive. <see cref="IndexDriveOpened.SettledDriveCount" /> gives each report's place in
    ///     settle order, so keep the report with the largest count rather than the last one
    ///     received. A declined or failed drive still counts toward the total and still reports.
    ///     Null (the default) reports and allocates nothing. Only the initial open reports;
    ///     <see cref="FileIndex.RescanAsync" /> stays silent, because its caller already awaits
    ///     the requested drives' results. Unlike <see cref="Progress" />, which samples only while a
    ///     producer runs, this fires on warm starts too. Marshalling belongs to the
    ///     <see cref="IProgress{T}" /> implementation, the same convention <see cref="Progress" />
    ///     uses.
    /// </summary>
    public IProgress<IndexDriveOpened>? OpenProgress { get; init; }

    /// <summary>
    ///     Receives human-readable block-file deletion lines and cache-tag mismatch lines.
    ///     A tag mismatch includes both stored and requested tags even when deletion fails.
    ///     Invoked synchronously on whatever thread performs the delete or encounters a mismatch,
    ///     which can be a snapshot release inside <see cref="FileIndex.DisposeAsync" />,
    ///     so a subscriber must be fast and non-blocking. Null (the default) logs nothing.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>
    ///     An optional consumer cache tag identifying the scan retention shape.
    ///     The tag is validated on construction: the FourCC must contain exactly four ASCII characters.
    ///     The default value is all zeros, meaning unspecified. Comparison matches both the FourCC and
    ///     version exactly. The consumer owns and increments its version whenever its scan shape changes.
    ///     MFTLib does not interpret the tag or compare scan filters.
    /// </summary>
    public CacheTag CacheTag { get; init; }

    /// <summary>
    ///     A test seam, owned by the index these options open: lists the cache directory's files
    ///     matching a pattern for the sweep of stale ".retired-*" siblings. Null (the default)
    ///     lists the real directory. The directory holds the index's owner lock file while the
    ///     sweep runs, so only a failure of the listing itself (a network share that drops, an
    ///     access-control change made meanwhile) reaches the sweep's handlers, and a test needs
    ///     this to stand in for one. Supplied through the options so the index is never handed
    ///     to test code before it has settled.
    /// </summary>
    internal Func<string, string, IEnumerable<string>>? EnumerateCacheFilesForTest { get; init; }
}
