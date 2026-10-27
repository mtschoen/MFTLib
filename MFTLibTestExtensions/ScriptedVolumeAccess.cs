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
    internal Func<VolumeKey, long, long, UsnJournalSettings> JournalGrow { get; init; } = UnsupportedGrow;

    internal static IEnumerable<IReadOnlyList<MftRecord>> UnsupportedScan(
        VolumeKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no scan source");
    }

    internal static IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> UnsupportedWatch(
        VolumeKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no watch source");
    }

    internal static UsnJournalSettings UnsupportedGrow(VolumeKey drive, long maximumSize, long allocationDelta)
    {
        throw new NotSupportedException("Broker has no journal grow source");
    }

    public UsnJournalCursor QueryCursor(VolumeKey drive) => CursorQuery(drive);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        VolumeKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        RecordBatches(drive, parseThreads, reporting, scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        VolumeKey drive, UsnJournalCursor since, int maximumBufferReads) =>
        JournalRead(drive, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        VolumeKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) => JournalWatch(drive, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(VolumeKey drive) => VolumeInformation(drive);
    public UsnJournalSettings GrowUsnJournal(VolumeKey drive, long maximumSize, long allocationDelta) =>
        JournalGrow(drive, maximumSize, allocationDelta);
}
