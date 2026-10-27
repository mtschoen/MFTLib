namespace MFTLib.Index;

/// <summary>
///     One drive's current state. <see cref="Watch" /> carries the live-watch state. The lawful combinations of
///     <see cref="State" />, <see cref="Block" /> and <see cref="Failure" />:
///     <list type="bullet">
///         <item><description><see cref="DriveState.Ready" /> with a block and no failure.</description></item>
///         <item><description><see cref="DriveState.Ready" /> with a retained block and a <see cref="DriveFailureKind.ProducerFailed" /> or <see cref="DriveFailureKind.MftSourceMissing" /> failure after a failed rescan or a failed catch-up retry scan during <see cref="FileIndex.OpenAsync" />.</description></item>
///         <item><description><see cref="DriveState.Offline" /> with neither.</description></item>
///         <item><description><see cref="DriveState.Failed" /> with a failure and no block.</description></item>
///     </list>
/// </summary>
public sealed record RootStatus
{
    /// <summary>Creates a status with the values every root has, whether or not it has a block or a watch.</summary>
    /// <param name="drive">The root identity.</param>
    /// <param name="state">Whether the root has a queryable block, is offline, or failed without a block.</param>
    internal RootStatus(RootKey drive, DriveState state)
    {
        Drive = drive;
        State = state;
    }

    /// <summary>Identifies the root of this <see cref="FileIndex" />, including when it is failed or offline.</summary>
    public RootKey Drive { get; init => field = value.Validated(); }

    /// <summary>Whether the drive has a queryable block, is offline, or failed without a block. Capacity exhaustion is reported independently by <see cref="DriveBlockStatus.IsCapacityExhausted" />.</summary>
    public DriveState State { get; init; }

    /// <summary>
    ///     Why this drive failed; null unless it did. Non-null while the drive is
    ///     <see cref="DriveState.Failed" />, and while a <see cref="DriveState.Ready" /> drive retains its previous
    ///     block after a failed <see cref="FileIndex.RescanAsync" /> or a failed catch-up
    ///     retry scan during <see cref="FileIndex.OpenAsync" />. A retained-block failure carries
    ///     <see cref="DriveFailureKind.ProducerFailed" /> or <see cref="DriveFailureKind.MftSourceMissing" />.
    ///     A successful scan publication clears it. A failed rescan of a cache-declined drive moves it to
    ///     the actual scan attempt's kind (<see cref="DriveFailureKind.ProducerFailed" /> or
    ///     <see cref="DriveFailureKind.MftSourceMissing" />), because the scan itself is now what failed.
    ///     An <see cref="DriveState.Offline" /> drive has none.
    /// </summary>
    public DriveFailure? Failure { get; init; }

    /// <summary>The block behind this status; null unless <see cref="State" /> is <see cref="DriveState.Ready" />.</summary>
    public DriveBlockStatus? Block { get; init; }

    /// <summary>This drive's watch capability and retained state, even when no watch is running. Never null.</summary>
    public DriveWatchStatus Watch { get; init; } = new();
}
