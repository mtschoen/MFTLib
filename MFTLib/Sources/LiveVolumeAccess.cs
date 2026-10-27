using MFTLib.Index;

namespace MFTLib;

sealed class LiveVolumeAccess : IVolumeAccess
{
    internal static LiveVolumeAccess Instance { get; } = new();
    LiveVolumeAccess() { }

    public UsnJournalCursor QueryCursor(VolumeKey volume) => LiveVolumeSources.QueryCursor(volume);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanVolumeRecordBatches(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        LiveVolumeSources.ScanVolumeRecordBatches(volume, parseThreads, reporting,
            scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads) =>
        LiveVolumeSources.ReadJournal(volume, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) =>
        LiveVolumeSources.WatchAndDisposeAsync(volume, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume) =>
        LiveVolumeSources.QueryVolumeInfo(volume);
    public UsnJournalSettings GrowUsnJournal(VolumeKey volume, long maximumSize, long allocationDelta) =>
        LiveVolumeSources.GrowUsnJournal(volume, maximumSize, allocationDelta);
}
