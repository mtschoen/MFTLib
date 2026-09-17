namespace MFTLib;

/// <summary>
///     One item on a drive's live watch channel inside the broker client: either a journal batch
///     to apply or the marker saying the backlog present when the drive's current arm started has
///     been delivered. Travelling on the same channel is what keeps the marker ordered after the
///     arm's last backlog batch. The public <see cref="JournalBatchSource" /> surface filters the
///     marker out; the index's watch source reads the full stream through
///     <see cref="LiveWatchItemSource" />.
/// </summary>
internal abstract record LiveWatchItem
{
    private protected LiveWatchItem()
    {
    }

    public sealed record Batch(UsnJournalEntry[] Entries, UsnJournalCursor Cursor) : LiveWatchItem;

    public sealed record CaughtUpMarker : LiveWatchItem;
}

/// <summary>
///     The item-level counterpart to <see cref="JournalBatchSource" />: yields a drive's batches
///     and its catch-up markers. Internal because the only consumer is
///     <see cref="BrokerIndexWatchSource" />; the public batch-only surface stays
///     <see cref="JournalBatchSource" />.
/// </summary>
internal delegate IAsyncEnumerable<LiveWatchItem> LiveWatchItemSource(
    string driveLetter,
    UsnJournalCursor since,
    CancellationToken cancellationToken);
