namespace MFTLib.Index;

/// <summary>
///     How to open an index for one root. For ordinary sources, <see cref="Root" /> is required;
///     a dump source supplies its own virtual root and requires <see cref="Root" /> to be null.
/// </summary>
public sealed record FileIndexOptions
{
    /// <summary>
    ///     The root to index. <see cref="FileIndex.OpenAsync" /> validates the root before creating a
    ///     cache directory or starting a scan. Required for ordinary sources; a dump source supplies
    ///     its own virtual root and requires this to be null.
    /// </summary>
    public IndexedRoot? Root { get; init; }

    /// <summary>
    ///     How the open uses the block cache. Null (the default) lets the library pick what fits the
    ///     source: <see cref="MFTLib.Index.CacheMode.Disabled" /> for a dump source and
    ///     <see cref="MFTLib.Index.CacheMode.Persistent" /> otherwise. A dump source rejects every explicit
    ///     value, <see cref="MFTLib.Index.CacheMode.Disabled" /> included.
    /// </summary>
    public CacheMode? CacheMode { get; init; }

    /// <summary>
    ///     An application-owned cache directory. Required and nonblank for
    ///     <see cref="MFTLib.Index.CacheMode.Persistent" /> and <see cref="MFTLib.Index.CacheMode.ExistingOnly" />.
    ///     Missing configuration causes <see cref="FileIndex.OpenAsync" /> to throw <see cref="ArgumentException" />.
    ///     Ignored, never resolved or created, with <see cref="MFTLib.Index.CacheMode.Disabled" />; dump sources
    ///     prohibit this option.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    ///     Supplies MFT-derived blocks and the root's live watch from one place. Null is legal at
    ///     admission and for warm cache adoption. An attempted MFT scan reports
    ///     <see cref="RootFailureKind.MftSourceMissing" /> when this is null; watching reports
    ///     <see cref="RootOperationOutcome.NotApplicable" /> without recording a watch request;
    ///     enumeration roots ignore the source. A rescan of a watched root stops its watch
    ///     when the replacement block commits and starts a fresh one from the new block's cursor through
    ///     the same source; a rescan whose production fails or is cancelled leaves a healthy watch running.
    /// </summary>
    public MftIndexSource? MftSource { get; init; }

    /// <summary>
    ///     Samples from whichever producer is building the root's block. Each scan, in
    ///     <see cref="FileIndex.OpenAsync" /> and in a rescan alike, ends with exactly one
    ///     <see cref="IndexScanPhase.Finished" /> sample whose <see cref="IndexScanProgress.Outcome" />
    ///     says whether it succeeded, failed or was cancelled. A handler that throws on the Finished sample is
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
    ///     Receives human-readable lines of five kinds: block-file deletions and failed deletions,
    ///     cache-tag and scan-shape mismatches (naming the stored and requested values even when
    ///     deletion fails), a rejected block left in place for a cache-only open, a block grown
    ///     during a directory walk, and a progress handler that threw on its Finished sample.
    ///     Invoked synchronously on whatever thread performs the delete or encounters the condition,
    ///     which can be a snapshot release inside <see cref="FileIndex.DisposeAsync" />,
    ///     so a subscriber must be fast and non-blocking. An exception the callback throws is
    ///     contained (except <see cref="OutOfMemoryException" />): it never replaces an operation's
    ///     result or exception and never escapes a release or <see cref="FileIndex.DisposeAsync" />.
    ///     Null (the default) logs nothing.
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
    ///     collection keeps directories only. A nonempty collection also keeps files whose names match by the
    ///     volume's comparison. MFT sources use NTFS name comparison, ordinal and ignoring case, including dumps
    ///     parsed on Linux. The directory walk uses one comparison for retained names and exact name/path pruning: ignoring case
    ///     on Windows and ordinal elsewhere. The root row is always kept. <see cref="FileIndex.OpenAsync" />
    ///     materializes the collection once and rejects a null or malformed UTF-16 element, or a set too long for the
    ///     source's request, before creating a cache directory or starting a scan. A cached block records
    ///     the retention that shaped it, so a warm start under another set discards the block and scans again.
    /// </summary>
    public IReadOnlyCollection<string>? RetainedFileNames { get; init; }

    /// <summary>
    ///     How enumeration roots walk: the directories they record but do not enter. MFT volumes ignore it.
    ///     <see cref="FileIndex.OpenAsync" /> rejects a null value and invalid rules (see
    ///     <see cref="DirectoryWalkOptions" />) before creating a cache directory or starting a scan. A cached
    ///     enumeration block records the options that shaped it, counting only pruned paths within its root's
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
        foreach (var name in names)
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

    internal static IndexedRoot ValidateRoot(FileIndexOptions options)
    {
        if (options.Root is not { } configuredRoot)
        {
            throw new ArgumentException("FileIndexOptions.Root must not be null.", nameof(options));
        }

        return configuredRoot.Validated();
    }
}
