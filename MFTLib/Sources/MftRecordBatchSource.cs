namespace MFTLib;

/// <summary>
///     Streams one drive's MFT records in bounded batches for the packed block output path.
///     <paramref name="parseThreads" /> is the scan's share of the host's parse threads, which the
///     host rewrites while the scan runs; <paramref name="operation" /> tells the host's watchdog
///     what the source is doing. <paramref name="scanOptions" /> carries the scan-wide choices a
///     source honors, so a new choice is a new member of that value and no source or test call
///     changes shape.
/// </summary>
internal delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(string driveLetter,
    ParseThreadAllowance parseThreads, IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
    MftRecordScanOptions scanOptions, CancellationToken cancellationToken);

/// <summary>The scan-wide choices a <see cref="MftRecordBatchSource" /> honors.</summary>
internal readonly record struct MftRecordScanOptions
{
    /// <summary>Whether the source also yields records that NTFS has freed, with <see cref="MftRecord.InUse" /> false.</summary>
    public bool IncludeFreed { get; init; }
}
