namespace MFTLib.Index;

/// <summary>
///     One change of a drive's watch state, as <see cref="FileIndex.WatchStateChanged" /> delivers
///     it. <paramref name="Watch" /> is the same <see cref="DriveWatchStatus" /> a consumer reads
///     from <see cref="DriveStatus.Watch" />, captured at the change: its
///     <see cref="DriveWatchStatus.State" /> is the new state and its
///     <see cref="DriveWatchStatus.StateVersion" /> increases by one with every change of that
///     drive's state, independently of every other drive, so of two snapshots of one drive the
///     one with the larger version is the later. One drive's changes are delivered in version
///     order. <paramref name="Fault" /> is the fault that caused the change, which
///     <see cref="FileIndex.WatchFaulted" /> reports after this, when there is one: a watch fault
///     moving the drive to <see cref="WatchState.Faulted" /> or <see cref="WatchState.Recovering" />,
///     a lost catch-up, a failed recovery, or a rescan whose replacement watch could not start. It
///     is null for a change a consumer's own call made, such as a start, a stop, a rescan, or a
///     start whose source threw, and for a watch catching up.
/// </summary>
/// <param name="Drive">The drive whose watch changed state.</param>
/// <param name="Watch">The drive's <see cref="DriveStatus.Watch" /> after the change.</param>
/// <param name="Fault">The fault that caused the change, or null when there is none.</param>
public sealed record WatchStateChange(DriveKey Drive, DriveWatchStatus Watch, WatchFault? Fault)
{
    /// <summary>Identifies the watch whose state changed; default keys are rejected.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); } = Drive.Validated();
}
