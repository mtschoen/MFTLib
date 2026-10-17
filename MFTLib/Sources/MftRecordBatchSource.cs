namespace MFTLib;

/// <summary>
///     Streams one drive's MFT records in bounded batches for the packed block output path.
///     <paramref name="parseThreads" /> is the scan's share of the host's parse threads, which the
///     host rewrites while the scan runs; <paramref name="reporting" /> carries the scan's progress and tells
///     the host's watchdog what the source is doing. <paramref name="scanOptions" /> carries the scan-wide choices a
///     source honors, so a new choice is a new member of that value and no source or test call
///     changes shape; a source yields freed records, with <see cref="MftRecord.InUse" /> false, only when
///     it sets <see cref="MftScanOptions.IncludeFreed" />. <paramref name="recordsOmitted" /> is told, before
///     the first batch, how many records the source left out because it could not trust them, so the
///     block write counts them with the records it skips.
/// </summary>
internal delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(string driveLetter,
    ParseThreadAllowance parseThreads, BlockWriteReporting reporting, MftScanOptions scanOptions,
    Action<long> recordsOmitted, CancellationToken cancellationToken);
