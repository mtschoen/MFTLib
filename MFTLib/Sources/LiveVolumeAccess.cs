namespace MFTLib;

internal sealed class LiveVolumeAccess : IVolumeAccess
{
    internal static LiveVolumeAccess Instance { get; } = new();
    private LiveVolumeAccess() { }

    public UsnJournalCursor QueryCursor(string drive) => LiveVolumeSources.QueryCursor(drive);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        string drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        LiveVolumeSources.ScanDriveRecordBatches(drive, parseThreads, reporting,
            scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        string drive, UsnJournalCursor since, int maximumBufferReads) =>
        LiveVolumeSources.ReadJournal(drive, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        string drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) =>
        LiveVolumeSources.WatchAndDisposeAsync(drive, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(string drive) =>
        LiveVolumeSources.QueryVolumeInfo(drive);
    public UsnJournalSettings GrowUsnJournal(string drive, long maximumSize, long allocationDelta) =>
        LiveVolumeSources.GrowUsnJournal(drive, maximumSize, allocationDelta);
}
