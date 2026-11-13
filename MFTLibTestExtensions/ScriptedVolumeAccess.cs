using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

sealed class ScriptedVolumeAccess : IVolumeAccess
{
    internal Func<VolumeKey, UsnJournalCursor> CursorQuery { get; init; } =
        _ => throw new NotSupportedException("Broker has no cursor source");
    internal MftRecordBatchSource RecordBatches { get; init; } = UnsupportedScan;
    internal Func<VolumeKey, UsnJournalCursor, int,
        (UsnJournalEntry[] Entries, UsnJournalCursor Updated)> JournalRead
    { get; init; } =
        (_, _, _) => throw new NotSupportedException("Broker has no scan source");
    internal Func<VolumeKey, UsnJournalCursor, ISourceOperationReporter, CancellationToken,
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>> JournalWatch
    { get; init; } = UnsupportedWatch;
    internal Func<VolumeKey, NtfsVolumeInformation> VolumeInformation { get; init; } =
        _ => throw new NotSupportedException("Broker has no volume information source");
    internal Func<VolumeKey, long, long, bool, UsnJournalGrowthResult> JournalGrow { get; init; } = UnsupportedGrow;

    internal static IEnumerable<IReadOnlyList<MftRecord>> UnsupportedScan(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no scan source");
    }

    internal static IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> UnsupportedWatch(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no watch source");
    }

    internal static UsnJournalGrowthResult UnsupportedGrow(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize)
    {
        throw new NotSupportedException("Broker has no journal grow source");
    }

    public UsnJournalCursor QueryCursor(VolumeKey volume) => CursorQuery(volume);
    public IMftSliceSource OpenVolumeScan(
        VolumeKey volume, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, CancellationToken cancellationToken) =>
        new MftRecordSliceSource(recordsOmitted =>
            RecordBatches(volume, parseThreads, reporting, scanOptions, recordsOmitted, cancellationToken));
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey volume, UsnJournalCursor since, int maximumBufferReads) =>
        JournalRead(volume, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchVolume(
        VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) => JournalWatch(volume, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(VolumeKey volume) => VolumeInformation(volume);
    public UsnJournalGrowthResult GrowUsnJournal(
        VolumeKey volume,
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false) =>
        JournalGrow(volume, maximumSize, allocationDelta, allowAboveMaximumRecommendedSize);
}
