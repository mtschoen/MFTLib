namespace MFTLib.Index;

/// <summary>
///     One drive's current state. <see cref="Watch" /> carries the live-watch state. The lawful combinations of
///     <see cref="State" />, <see cref="Block" /> and <see cref="Failure" />:
///     <list type="bullet">
///         <item><description><see cref="DriveState.Ready" /> with a block and no failure.</description></item>
///         <item><description><see cref="DriveState.Ready" /> with a retained block and a <see cref="DriveFailureKind.ProducerFailed" /> failure after a failed rescan or a failed catch-up retry scan during <see cref="FileIndex.OpenAsync" />.</description></item>
///         <item><description><see cref="DriveState.Offline" /> with neither.</description></item>
///         <item><description><see cref="DriveState.Failed" /> with a failure and no block.</description></item>
///     </list>
/// </summary>
public sealed record DriveStatus
{
    /// <summary>Creates a status with the values every drive has, whether or not it has a block or a watch.</summary>
    /// <param name="driveLetter">The drive.</param>
    /// <param name="state">Whether the drive has a queryable block, is offline, or failed without a block.</param>
    internal DriveStatus(DriveKey driveLetter, DriveState state)
    {
        Drive = driveLetter;
        State = state;
    }

    /// <summary>Identifies the drive in every per-drive <see cref="FileIndex" /> call; one status exists per configured drive, including failed and offline ones.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); }

    /// <summary>Whether the drive has a queryable block, is offline, or failed without a block. Compaction is reported independently by <see cref="DriveBlockStatus.IsCompactionNeeded" />.</summary>
    public DriveState State { get; init; }

    /// <summary>
    ///     Why this drive failed; null unless it did. Non-null while the drive is
    ///     <see cref="DriveState.Failed" />, and while a <see cref="DriveState.Ready" /> drive retains its previous
    ///     block after a failed <see cref="FileIndex.RescanAsync" /> or a failed catch-up
    ///     retry scan during <see cref="FileIndex.OpenAsync" />. Both retained-block failures read
    ///     <see cref="DriveFailureKind.ProducerFailed" />. A successful scan publication clears it. A failed
    ///     rescan of a cache-declined drive moves it to <see cref="DriveFailureKind.ProducerFailed" />, because
    ///     the scan itself is now what failed. An <see cref="DriveState.Offline" /> drive has none.
    /// </summary>
    public DriveFailure? Failure { get; init; }

    /// <summary>The block behind this status; null unless <see cref="State" /> is <see cref="DriveState.Ready" />.</summary>
    public DriveBlockStatus? Block { get; init; }

    /// <summary>This drive's watch capability and retained state, even when no watch is running. Never null.</summary>
    public DriveWatchStatus Watch { get; init; } = new();
}
