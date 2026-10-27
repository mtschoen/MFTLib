namespace MFTLib.Index;

/// <summary>
///     One item a root's <see cref="IIndexVolumeWatch" /> yields: a <see cref="JournalBatch" /> to
///     apply, or the <see cref="VolumeCaughtUp" /> marker. The handle belongs to one root, so no
///     item names one. A failure is not an item: the handle's read throws instead.
/// </summary>
abstract record WatchStreamItem
{
    private protected WatchStreamItem()
    {
    }
}

/// <summary>
///     The journal backlog present when the volume's watch started has been delivered in full;
///     every batch after this marker is a live entry. A handle yields it once, immediately when
///     there is no backlog.
/// </summary>
sealed record VolumeCaughtUp : WatchStreamItem;
