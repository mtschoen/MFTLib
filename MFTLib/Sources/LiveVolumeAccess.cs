using MFTLib.Index;

namespace MFTLib;

sealed class LiveVolumeAccess : IVolumeAccess
{
    internal static LiveVolumeAccess Instance { get; } = new();
    LiveVolumeAccess() { }

    public UsnJournalCursor QueryCursor(VolumeKey drive) => LiveVolumeSources.QueryCursor(drive);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        VolumeKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        LiveVolumeSources.ScanDriveRecordBatches(drive, parseThreads, reporting,
            scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey drive, UsnJournalCursor since, int maximumBufferReads) =>
        LiveVolumeSources.ReadJournal(drive, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        VolumeKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) =>
        LiveVolumeSources.WatchAndDisposeAsync(drive, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(VolumeKey drive) =>
        LiveVolumeSources.QueryVolumeInfo(drive);
    public UsnJournalSettings GrowUsnJournal(VolumeKey drive, long maximumSize, long allocationDelta) =>
        LiveVolumeSources.GrowUsnJournal(drive, maximumSize, allocationDelta);
}
