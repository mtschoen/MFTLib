using MFTLib.Index;

namespace MFTLib;

sealed class LiveVolumeAccess : IVolumeAccess
{
    internal static LiveVolumeAccess Instance { get; } = new();
    LiveVolumeAccess() { }

    public UsnJournalCursor QueryCursor(VolumeKey volume) => LiveVolumeSources.QueryCursor(volume);
    public IMftSliceSource OpenVolumeScan(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, ulong expectedVolumeSerial, CancellationToken cancellationToken) =>
        LiveVolumeSources.OpenVolumeScan(volume, parseThreads, reporting, scanOptions, expectedVolumeSerial, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads) =>
        LiveVolumeSources.ReadJournal(volume, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) =>
        LiveVolumeSources.WatchAndDisposeAsync(volume, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume) =>
        LiveVolumeSources.QueryVolumeInfo(volume);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ReadInlineContent(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken) =>
        LiveVolumeSources.ReadInlineContent(volume, volumeSerial, requests, sink, cancellationToken);
    public UsnJournalGrowthResult GrowUsnJournal(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false) =>
        LiveVolumeSources.GrowUsnJournal(volume, maximumSize, allocationDelta, allowAboveMaximumRecommendedSize);
}
