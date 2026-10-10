namespace MFTLib.Index;

/// <summary>
///     One drive's current state. Online status carries block-header data in <see cref="Block" />;
///     blockless status carries zero values there. <see cref="Watch" /> carries the live-watch state.
/// </summary>
public sealed record DriveStatus
{
    /// <summary>Creates a status with the values every drive has, whether or not it has a block or a watch.</summary>
    /// <param name="driveLetter">The drive.</param>
    /// <param name="state">Whether the drive can answer queries, needs a rescan, or has no block.</param>
    internal DriveStatus(char driveLetter, DriveState state)
    {
        DriveLetter = driveLetter;
        State = state;
    }

    /// <summary>Identifies the drive in every per-drive <see cref="FileIndex" /> call; one status exists per configured drive, including failed and offline ones.</summary>
    public char DriveLetter { get; init; }

    /// <summary>Whether the drive can answer queries, needs a rescan, or has no block.</summary>
    public DriveState State { get; init; }

    /// <summary>
    ///     Why this drive is <see cref="DriveState.Failed" />:
    ///     <see cref="DriveFailureKind.CacheDeclined" /> when a cache-only open declined it for
    ///     lack of a usable cache block, <see cref="DriveFailureKind.InUse" /> when the cache
    ///     block is locked by another live index, <see cref="DriveFailureKind.ProducerFailed" />
    ///     when its MFT producer failed. <see cref="DriveFailureKind.None" /> in every other
    ///     state, including <see cref="DriveState.Offline" />. A successful
    ///     <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> of a failed drive clears this back to
    ///     <see cref="DriveFailureKind.None" /> along with the state; a failed rescan of a
    ///     cache-declined drive moves it to <see cref="DriveFailureKind.ProducerFailed" />,
    ///     because the scan itself is now what failed.
    /// </summary>
    public DriveFailureKind FailureKind { get; init; }

    /// <summary>
    ///     The detail behind <see cref="FailureKind" />: the MFT producer's error when the
    ///     producer failed during opening or its latest rescan, or the cache-only refusal when
    ///     <see cref="FileIndexOptions.InitialOpenCacheOnly" /> declined the drive. A drive that
    ///     fails during opening has <see cref="DriveState.Failed" /> and no block. A failed
    ///     rescan leaves the previous block in place. Null after a successful MFT production,
    ///     when enumeration was selected explicitly, or on a warm start.
    /// </summary>
    public string? FailureMessage { get; init; }

    /// <summary>The block behind this status; zero values for a drive with no block. Never null.</summary>
    public DriveBlockStatus Block { get; init; } = new();

    /// <summary>This drive's live-watch state; defaults for a drive that is not watched. Never null.</summary>
    public DriveWatchStatus Watch { get; init; } = new();

    /// <summary>
    ///     Converts this captured snapshot to the watch event shape, preserving its drive, catch-up
    ///     state and state version without rereading the index. The fault is null because a snapshot
    ///     does not retain the event's fault; <see cref="DriveWatchStatus.FailureMessage" /> is not converted to one.
    /// </summary>
    /// <returns>The captured watch state, with no event fault.</returns>
    public DriveWatchState ToWatchState() =>
        new(DriveLetter, Watch.CatchUpState, Watch.StateVersion, null);
}
