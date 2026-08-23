namespace MFTLib;

/// <summary>
///     Aggregated result of
///     <see
///         cref="JournalBrokerClient.ArmScanAndCatchUpAsync(IReadOnlyList{string}, BrokerScanOptions?, CancellationToken)" />
///     :
///     the armed cursor (captured before the scan began), the advanced cursor after catch-up,
///     the catch-up journal entries, and any per-drive error messages.
/// </summary>
public sealed class BrokerScanResult(
    IReadOnlyDictionary<string, UsnJournalCursor> armedCursors,
    IReadOnlyDictionary<string, UsnJournalCursor> advancedCursors,
    IReadOnlyDictionary<string, UsnJournalEntry[]> catchUpEntries,
    IReadOnlyDictionary<string, string> errors)
{

    /// <summary>Per-drive cursor captured before the scan (journalId:nextUsn).</summary>
    public IReadOnlyDictionary<string, UsnJournalCursor> ArmedCursors { get; } = armedCursors;

    /// <summary>Per-drive cursor advanced past the catch-up batch.</summary>
    public IReadOnlyDictionary<string, UsnJournalCursor> AdvancedCursors { get; } = advancedCursors;

    /// <summary>Per-drive catch-up journal entries received after the scan.</summary>
    public IReadOnlyDictionary<string, UsnJournalEntry[]> CatchUpEntries { get; } = catchUpEntries;

    /// <summary>Per-drive error messages for drives that the broker could not scan.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}
