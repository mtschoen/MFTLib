namespace MFTLib;

/// <summary>
///     Streams one drive's MFT records in bounded batches for the packed block output path.
///     <paramref name="parseThreads" /> is the scan's share of the host's parse threads, which the
///     host rewrites while the scan runs; <paramref name="operation" /> tells the host's watchdog
///     what the source is doing.
/// </summary>
public delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(string driveLetter,
    ParseThreadAllowance parseThreads, IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
    CancellationToken cancellationToken);
