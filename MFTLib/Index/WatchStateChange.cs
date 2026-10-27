namespace MFTLib.Index;

/// <summary>
///     One change of a root's watch state, as <see cref="FileIndex.WatchStateChanged" /> delivers
///     it. <see cref="Watch" /> is the same <see cref="RootWatchStatus" /> a consumer reads
///     from <see cref="RootStatus.Watch" />, captured at the change: its
///     <see cref="RootWatchStatus.State" /> is the new state and its
///     <see cref="RootWatchStatus.StateVersion" /> increases by one with every change of that
///     root's state, independently of every other root, so of two snapshots of one root the
///     one with the larger version is the later. One root's changes are delivered in version
///     order. <see cref="Fault" /> is the fault that caused the change, which
///     <see cref="FileIndex.WatchFaulted" /> reports after this, when there is one: a watch fault
///     moving the root to <see cref="WatchState.Faulted" /> or <see cref="WatchState.Recovering" />,
///     a lost catch-up, a failed recovery, or a rescan whose replacement watch could not start. It
///     is null for a change a consumer's own call made, such as a start, a stop, a rescan, or a
///     start whose source threw, and for a watch catching up.
/// </summary>
public sealed record WatchStateChange
{
    /// <summary>Creates a report of one root's watch state change.</summary>
    /// <param name="volume">The root whose watch changed state.</param>
    /// <param name="watch">The root's watch status after the change.</param>
    /// <param name="fault">The fault that caused the change, or null.</param>
    internal WatchStateChange(VolumeKey volume, RootWatchStatus watch, WatchFault? fault)
    {
        Volume = volume;
        Watch = watch;
        Fault = fault;
    }

    /// <summary>Identifies the watch whose state changed; default keys are rejected.</summary>
    public VolumeKey Volume { get; init => field = value.Validated(); }

    /// <summary>The root's <see cref="RootStatus.Watch" /> after the change.</summary>
    public RootWatchStatus Watch { get; init; }

    /// <summary>The fault that caused the change, or null when there is none.</summary>
    public WatchFault? Fault { get; init; }
}
