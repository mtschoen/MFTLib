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

    /// <summary>
    ///     Opens the volume for a scan whose slices <see cref="MftBlockRowWriter" /> writes while it parses. The scan
    ///     starts when its slices are enumerated; disposing the source waits for it to end and closes the volume.
    /// </summary>
    IMftSliceSource OpenVolumeScan(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, CancellationToken cancellationToken);

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
