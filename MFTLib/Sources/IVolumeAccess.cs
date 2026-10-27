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
    UsnJournalCursor QueryCursor(VolumeKey drive);
    IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        VolumeKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted,
        CancellationToken cancellationToken);
    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey drive, UsnJournalCursor since, int maximumBufferReads);
    IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        VolumeKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken);
    NtfsVolumeInformation QueryVolumeInformation(VolumeKey drive);
    UsnJournalSettings GrowUsnJournal(VolumeKey drive, long maximumSize, long allocationDelta);
}
