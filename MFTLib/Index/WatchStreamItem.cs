namespace MFTLib.Index;

/// <summary>
///     One item a drive's <see cref="IIndexDriveWatch" /> yields: a <see cref="JournalBatch" /> to
///     apply, or the <see cref="DriveCaughtUp" /> marker. The handle belongs to one drive, so no
///     item names one. A failure is not an item: the handle's read throws instead.
/// </summary>
internal abstract record WatchStreamItem
{
    private protected WatchStreamItem()
    {
    }
}

/// <summary>
///     The journal backlog present when the drive's watch started has been delivered in full;
///     every batch after this marker is a live entry. A handle yields it once, immediately when
///     there is no backlog.
/// </summary>
internal sealed record DriveCaughtUp : WatchStreamItem;
