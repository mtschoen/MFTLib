namespace MFTLib;

/// <summary>
/// Production wires this to <c>MftVolume.WatchUsnJournalWithCursor</c>;
/// tests inject an in-memory async stream so they can drive the watcher
/// loop without a real elevated volume handle. Exposed as public so callers
/// (e.g. a broker client) can pass a broker-backed source into their own
/// journal-watching loop.
/// </summary>
public delegate IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> JournalBatchSource(
    string driveLetter,
    UsnJournalCursor since,
    CancellationToken cancellationToken);
