using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

internal sealed class ScriptedVolumeAccess : IVolumeAccess
{
    internal Func<DriveKey, UsnJournalCursor> CursorQuery { get; init; } =
        _ => throw new NotSupportedException("Broker has no cursor source");
    internal MftRecordBatchSource RecordBatches { get; init; } = UnsupportedScan;
    internal Func<DriveKey, UsnJournalCursor, int,
        (UsnJournalEntry[] Entries, UsnJournalCursor Updated)> JournalRead
    { get; init; } =
        (_, _, _) => throw new NotSupportedException("Broker has no scan source");
    internal Func<DriveKey, UsnJournalCursor, ISourceOperationReporter, CancellationToken,
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>> JournalWatch
    { get; init; } = UnsupportedWatch;
    internal Func<DriveKey, NtfsVolumeInformation> VolumeInformation { get; init; } =
        _ => throw new NotSupportedException("Broker has no volume information source");
    internal Func<DriveKey, long, long, UsnJournalSettings> JournalGrow { get; init; } = UnsupportedGrow;

    internal static IEnumerable<IReadOnlyList<MftRecord>> UnsupportedScan(
        DriveKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no scan source");
    }

    internal static IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> UnsupportedWatch(
        DriveKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Broker has no watch source");
    }

    internal static UsnJournalSettings UnsupportedGrow(DriveKey drive, long maximumSize, long allocationDelta)
    {
        throw new NotSupportedException("Broker has no journal grow source");
    }

    public UsnJournalCursor QueryCursor(DriveKey drive) => CursorQuery(drive);
    public IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        DriveKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken) =>
        RecordBatches(drive, parseThreads, reporting, scanOptions, recordsOmitted, cancellationToken);
    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(
        DriveKey drive, UsnJournalCursor since, int maximumBufferReads) =>
        JournalRead(drive, since, maximumBufferReads);
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDrive(
        DriveKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        CancellationToken cancellationToken) => JournalWatch(drive, since, operation, cancellationToken);
    public NtfsVolumeInformation QueryVolumeInformation(DriveKey drive) => VolumeInformation(drive);
    public UsnJournalSettings GrowUsnJournal(DriveKey drive, long maximumSize, long allocationDelta) =>
        JournalGrow(drive, maximumSize, allocationDelta);
}
