using MFTLib.Index;

namespace MFTLib;

/// <summary>
/// Internal volume operations required by block production and the broker host.
/// Methods may throw, streams stay lazy, disposal terminates their volume lifetime,
/// the scan preserves the mutable allowance/options/cancellation token, and bounded
/// journal reads preserve their maximum-buffer-read contract.
/// </summary>
internal interface IVolumeAccess
{
    UsnJournalCursor QueryCursor(DriveKey drive);
    IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        DriveKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted,
        CancellationToken cancellationToken);
    (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        DriveKey drive, UsnJournalCursor since, int maximumBufferReads);
    IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        DriveKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken);
    NtfsVolumeInformation QueryVolumeInformation(DriveKey drive);
    UsnJournalSettings GrowUsnJournal(DriveKey drive, long maximumSize, long allocationDelta);
}
