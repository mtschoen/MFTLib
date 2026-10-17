using MFTLib;

namespace MFTLibTestExtensions;

internal sealed class ScriptedVolumeAccess : IVolumeAccess
{
    internal Func<string, UsnJournalCursor> CursorQuery { get; init; } =
        _ => throw new NotSupportedException("Broker has no cursor source");
    internal MftRecordBatchSource RecordBatches { get; init; } = UnsupportedScan;
    internal Func<string, UsnJournalCursor, int,
        (UsnJournalEntry[] Entries, UsnJournalCursor Updated)> JournalRead
    { get; init; } =
        (_, _, _) => throw new NotSupportedException("Broker has no scan source");
    internal Func<string, UsnJournalCursor, ISourceOperationReporter, CancellationToken,
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>> JournalWatch
    { get; init; } = UnsupportedWatch;
    internal Func<string, NtfsVolumeInformation> VolumeInformation { get; init; } =
        _ => throw new NotSupportedException("Broker has no volume information source");
    internal Func<string, long, long, UsnJournalSettings> JournalGrow { get; init; } = UnsupportedGrow;

    internal static IEnumerable<IReadOnlyList<MftRecord>> UnsupportedScan(
        string drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no scan source");
    }

    internal static IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> UnsupportedWatch(
        string drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no watch source");
    }

    internal static UsnJournalSettings UnsupportedGrow(string drive, long maximumSize, long allocationDelta)
    {
        throw new NotSupportedException("Broker has no journal grow source");
    }

    public UsnJournalCursor QueryCursor(string drive) => CursorQuery(drive);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        string drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        RecordBatches(drive, parseThreads, reporting, scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        string drive, UsnJournalCursor since, int maximumBufferReads) =>
        JournalRead(drive, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        string drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) => JournalWatch(drive, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(string drive) => VolumeInformation(drive);
    public UsnJournalSettings GrowUsnJournal(string drive, long maximumSize, long allocationDelta) =>
        JournalGrow(drive, maximumSize, allocationDelta);
}
