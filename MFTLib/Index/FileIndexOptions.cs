namespace MFTLib.Index;

/// <summary>
///     How to open an index. For ordinary sources, an empty drive set opens an index with no
///     drives; a dump source supplies its own virtual drive and requires an empty drive set.
/// </summary>
public sealed record FileIndexOptions
{
    /// <summary>
    ///     Drives to include in the index. <see cref="FileIndex.OpenAsync" /> materializes the
    ///     collection once and rejects a null collection, a null or invalid element, or duplicate
    ///     normalized <see cref="DriveKey" /> entries before creating a cache directory or starting
    ///     a scan. For ordinary sources, an empty collection opens an index with no drives; a dump
    ///     source supplies its own virtual drive and requires an empty collection.
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
    ///     Supplies MFT-derived blocks and each drive's live watch from one place. Null is legal at
    ///     admission and for warm cache adoption. An attempted MFT scan reports
    ///     <see cref="DriveFailureKind.MftSourceMissing" /> when this is null; watching reports
    ///     <see cref="DriveOperationOutcome.NotApplicable" /> without recording a watch request;
    ///     enumeration drives ignore the source. A rescan of a watched drive stops that drive's watch
    ///     when the replacement block commits and starts a fresh one from the new block's cursor through
    ///     the same source; a rescan whose production fails or is cancelled leaves a healthy watch running.
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
    public IProgress<IndexScanProgress>? ScanProgress { get; init; }

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
    ///     the requested drives' results. Unlike <see cref="ScanProgress" />, which samples only while a
    ///     producer runs, this fires on warm starts too. Marshalling belongs to the
    ///     <see cref="IProgress{T}" /> implementation, the same convention <see cref="ScanProgress" />
    ///     uses.
    /// </summary>
    public IProgress<IndexDriveOpened>? DriveOpenProgress { get; init; }

    /// <summary>
    ///     Receives human-readable block-file deletion lines and cache-tag mismatch lines.
    ///     A tag mismatch includes both stored and requested tags even when deletion fails.
    ///     Invoked synchronously on whatever thread performs the delete or encounters a mismatch,
    ///     which can be a snapshot release inside <see cref="FileIndex.DisposeAsync" />,
    ///     so a subscriber must be fast and non-blocking. Null (the default) logs nothing.
    /// </summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>
    ///     An optional consumer cache tag carrying meaning only the consumer can see. The tag is validated on
    ///     construction: the FourCC must contain exactly four ASCII characters. The default value is all zeros,
    ///     meaning unspecified. Comparison matches both the FourCC and version exactly. MFTLib does not
    ///     interpret the tag. The options that shape a block's rows (<see cref="RetainedFileNames" />,
    ///     <see cref="DirectoryWalk" /> and <see cref="MftScanOptions.IncludeFreed" />) are compared by the
    ///     library itself as the block's scan shape, so the tag need not change with them.
    /// </summary>
    public CacheTag CacheTag { get; init; }

    /// <summary>
    ///     Which files the index keeps, applied by every producer: the MFT producers filter records and
    ///     the directory walk filters entries. Null (the default) keeps every eligible row. An empty
    ///     collection keeps directories only. A nonempty collection also keeps files whose names match
    ///     ordinally, ignoring case. The root row is always kept. <see cref="FileIndex.OpenAsync" />
    ///     materializes the collection once and rejects a null or malformed UTF-16 element, or a set too long for the
    ///     source's request, before creating a cache directory or starting a scan. A cached block records
    ///     the retention that shaped it, so a warm start under another set discards the block and scans again.
    /// </summary>
    public IReadOnlyCollection<string>? RetainedFileNames { get; init; }

    /// <summary>
    ///     How enumeration drives walk: the directories they record but do not enter. MFT drives ignore it.
    ///     <see cref="FileIndex.OpenAsync" /> rejects a null value and invalid rules (see
    ///     <see cref="DirectoryWalkOptions" />) before creating a cache directory or starting a scan. A cached
    ///     enumeration block records the options that shaped it, counting only pruned paths within its drive's
    ///     root, so a warm start under other options discards the block and scans again.
    /// </summary>
    public DirectoryWalkOptions DirectoryWalk { get; init; } = new();

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    ///     Copies <see cref="RetainedFileNames" /> once so the index never re-evaluates or shares the
    ///     caller's collection, and rejects a null element.
    /// </summary>
    internal static string[]? ValidateRetainedFileNames(FileIndexOptions options)
    {
        if (options.RetainedFileNames is not { } names)
        {
            return null;
        }

        var copy = new List<string>(names.Count);
        foreach (string? name in names)
        {
            if (name is null)
            {
                throw new ArgumentException("FileIndexOptions.RetainedFileNames must not contain null entries.",
                    nameof(options));
            }

            if (!WellFormedText.IsWellFormed(name))
            {
                throw new ArgumentException(
                    "FileIndexOptions.RetainedFileNames must not contain an entry with an unpaired surrogate.",
                    nameof(options));
            }

            copy.Add(name);
        }

        return [.. copy];
    }

    internal static IndexedDrive[] ValidateDrives(FileIndexOptions options)
    {
        if (options.Drives is not { } configuredDrives)
        {
            throw new ArgumentException("FileIndexOptions.Drives must not be null.", nameof(options));
        }

        // Materialized once into a private copy so the index never re-evaluates or shares the
        // caller's list, and validated before any cache directory or scan work begins.
        var drives = configuredDrives.ToArray();
        HashSet<DriveKey> seen = [];
        foreach (var drive in drives)
        {
            if (drive is null)
            {
                throw new ArgumentException("FileIndexOptions.Drives must not contain null entries.",
                    nameof(options));
            }

            var driveKey = drive.Validated().Drive;
            if (!seen.Add(driveKey))
            {
                throw new ArgumentException($"Drive {driveKey} is listed more than once.", nameof(options));
            }
        }

        return drives;
    }
}
