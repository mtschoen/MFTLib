using MFTLib.Index;

namespace MFTLib;

/// <summary>
/// Internal volume operations required by block production and the broker host.
/// Methods may throw, streams stay lazy, disposal terminates their volume lifetime,
/// the scan preserves the mutable allowance/options/cancellation token, and bounded
/// journal reads preserve their maximum-buffer-read contract.
/// </summary>
interface IVolumeAccess
{
    UsnJournalCursor QueryCursor(VolumeKey volume);
    IEnumerable<IReadOnlyList<MftRecord>> ScanVolumeRecordBatches(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Scans the volume straight into <paramref name="writer" />'s block. The default writes the record batches
    ///     of <see cref="ScanVolumeRecordBatches" />; the live volume streams its parse into the block instead.
    /// </summary>
    BlockWriteResult WriteVolumeRows(
        BlockWriter writer, VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        MftBlockRowWriter.WriteBatches(writer,
            ScanVolumeRecordBatches(volume, parseThreads, reporting, scanOptions, recordsOmitted, cancellationToken),
            scanOptions, reporting.Progress, cancellationToken);

    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads);
    IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken);
    NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume);
    UsnJournalGrowthResult GrowUsnJournal(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false);
}
