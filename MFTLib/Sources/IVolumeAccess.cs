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
    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads);
    IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken);
    NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume);
    UsnJournalSettings GrowUsnJournal(VolumeKey volume, long maximumSize, long allocationDelta);
}
