namespace MFTLib.Index;

/// <summary>
///     One change of a drive's <see cref="DriveStatus.WatchCatchUpState" />, as
///     <see cref="FileIndex.WatchStateChanged" /> delivers it. <paramref name="WatchStateVersion" /> is the
///     drive's <see cref="DriveStatus.WatchStateVersion" /> after the change: it increases by one
///     with every change of that drive's state, independently of every other drive, so of two
///     states of one drive the one with the larger version is the later. One drive's states are
///     delivered in version order. <paramref name="Fault" />
///     is the fault that caused the change, which <see cref="FileIndex.WatchFaulted" /> reports
///     after this, when there is one: a watch fault moving the drive to
///     <see cref="WatchCatchUpState.Faulted" /> or <see cref="WatchCatchUpState.Recovering" />, a
///     lost catch-up, a failed recovery, or a rescan whose replacement watch could not start. It is
///     null for a change a consumer's own call made, such as a start, a stop, a rescan, or a start
///     whose source threw, and for a watch catching up.
/// </summary>
/// <param name="DriveLetter">The drive whose watch changed state.</param>
/// <param name="WatchCatchUpState">The drive's <see cref="DriveStatus.WatchCatchUpState" /> after the change.</param>
/// <param name="WatchStateVersion">The drive's <see cref="DriveStatus.WatchStateVersion" /> after the change; larger is later for one drive.</param>
/// <param name="Fault">The fault that caused the change, or null when there is none.</param>
public sealed record DriveWatchState(char DriveLetter, WatchCatchUpState WatchCatchUpState, long WatchStateVersion, WatchFault? Fault);
