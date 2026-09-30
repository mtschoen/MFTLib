namespace MFTLib;

/// <summary>
///     Production wires this to <c>MftVolume.WatchUsnJournalWithCursor</c>; tests inject an
///     in-memory async stream so they can drive the host's watch without a real elevated volume
///     handle. <paramref name="operation" /> tells the host's watchdog whether the source is
///     waiting on the volume or processing a batch.
/// </summary>
public delegate IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> JournalBatchSource(
    string driveLetter,
    UsnJournalCursor since,
    IBrokerOperationReporter operation,
    CancellationToken cancellationToken);
