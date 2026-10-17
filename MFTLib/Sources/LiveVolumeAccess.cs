using MFTLib.Index;

namespace MFTLib;

internal sealed class LiveVolumeAccess : IVolumeAccess
{
    internal static LiveVolumeAccess Instance { get; } = new();
    private LiveVolumeAccess() { }

    public UsnJournalCursor QueryCursor(DriveKey drive) => LiveVolumeSources.QueryCursor(drive);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        DriveKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        LiveVolumeSources.ScanDriveRecordBatches(drive, parseThreads, reporting,
            scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        DriveKey drive, UsnJournalCursor since, int maximumBufferReads) =>
        LiveVolumeSources.ReadJournal(drive, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        DriveKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) =>
        LiveVolumeSources.WatchAndDisposeAsync(drive, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(DriveKey drive) =>
        LiveVolumeSources.QueryVolumeInfo(drive);
    public UsnJournalSettings GrowUsnJournal(DriveKey drive, long maximumSize, long allocationDelta) =>
        LiveVolumeSources.GrowUsnJournal(drive, maximumSize, allocationDelta);
}
