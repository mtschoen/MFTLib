using MFTLib.Index;

namespace MFTLib;

sealed class LiveVolumeAccess : IVolumeAccess
{
    internal static LiveVolumeAccess Instance { get; } = new();
    LiveVolumeAccess() { }

    public UsnJournalCursor QueryCursor(VolumeKey volume) => LiveVolumeSources.QueryCursor(volume);
    public IMftSliceSource OpenVolumeScan(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, CancellationToken cancellationToken) =>
        LiveVolumeSources.OpenVolumeScan(volume, parseThreads, reporting, scanOptions, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads) =>
        LiveVolumeSources.ReadJournal(volume, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) =>
        LiveVolumeSources.WatchAndDisposeAsync(volume, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume) =>
        LiveVolumeSources.QueryVolumeInfo(volume);
    public UsnJournalGrowthResult GrowUsnJournal(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false) =>
        LiveVolumeSources.GrowUsnJournal(volume, maximumSize, allocationDelta, allowAboveMaximumRecommendedSize);
}
