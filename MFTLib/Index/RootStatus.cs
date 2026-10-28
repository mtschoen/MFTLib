namespace MFTLib.Index;

/// <summary>
///     One root's current state. <see cref="Watch" /> carries the live-watch state. The lawful combinations of
///     <see cref="State" />, <see cref="Block" /> and <see cref="Failure" />:
///     <list type="bullet">
///         <item><description><see cref="RootState.Ready" /> with a block and no failure.</description></item>
///         <item><description><see cref="RootState.Ready" /> with a retained block and a <see cref="RootFailureKind.ProducerFailed" /> or <see cref="RootFailureKind.MftUnavailable" /> failure after a failed rescan or a failed catch-up retry scan during <see cref="FileIndex.OpenAsync" />.</description></item>
///         <item><description><see cref="RootState.Offline" /> with neither.</description></item>
///         <item><description><see cref="RootState.Failed" /> with a failure and no block.</description></item>
///     </list>
/// </summary>
public sealed record RootStatus
{
    /// <summary>Creates a status with the values every root has, whether or not it has a block or a watch.</summary>
    /// <param name="root">The root identity.</param>
    /// <param name="state">Whether the root has a queryable block, is offline, or failed without a block.</param>
    internal RootStatus(RootKey root, RootState state)
    {
        Root = root;
        State = state;
    }

    /// <summary>Identifies the root of this <see cref="FileIndex" />, including when it is failed or offline.</summary>
    public RootKey Root { get; init => field = value.Validated(); }

    /// <summary>
    ///     The containing volume's reported serial, or null before volume facts are read or when the platform
    ///     reports none (including Linux roots, network shares, unlettered mounts, unavailable volumes and dump sources).
    ///     A root that is <see cref="RootState.Offline" /> at open reports null. A reported zero is null;
    ///     no substitute identity is generated.
    /// </summary>
    public uint? VolumeSerial { get; init; }

    /// <summary>Whether the root has a queryable block, is offline, or failed without a block. Capacity exhaustion is reported independently by <see cref="RootBlockStatus.IsCapacityExhausted" />.</summary>
    public RootState State { get; init; }

    /// <summary>
    ///     Why this root failed; null unless it did. Non-null while the root is
    ///     <see cref="RootState.Failed" />, and while a <see cref="RootState.Ready" /> root retains its previous
    ///     block after a failed <see cref="FileIndex.RescanAsync" /> or a failed catch-up
    ///     retry scan during <see cref="FileIndex.OpenAsync" />. A retained-block failure carries
    ///     <see cref="RootFailureKind.ProducerFailed" /> or <see cref="RootFailureKind.MftUnavailable" />.
    ///     A successful scan publication clears it. A failed rescan of a cache-declined root moves it to
    ///     the actual scan attempt's kind (<see cref="RootFailureKind.ProducerFailed" /> or
    ///     <see cref="RootFailureKind.MftUnavailable" />), because the scan itself is now what failed.
    ///     An <see cref="RootState.Offline" /> root has none.
    /// </summary>
    public RootFailure? Failure { get; init; }

    /// <summary>The block behind this status; null unless <see cref="State" /> is <see cref="RootState.Ready" />.</summary>
    public RootBlockStatus? Block { get; init; }

    /// <summary>This root's watch capability and retained state, even when no watch is running. Never null.</summary>
    public RootWatchStatus Watch { get; init; } = new();

    /// <summary>
    ///     The producer selected at open and fixed for the index's life; null only for a root that was
    ///     <see cref="RootState.Offline" /> at open, which never reached selection. A failed MFT scan does not change it.
    /// </summary>
    public ProducerKind? Producer { get; init; }

    /// <summary>
    ///     Why the MFT could not be used, or null. Set from the volume facts when <see cref="ProducerSelection.Automatic" />
    ///     walked or <see cref="ProducerSelection.MftOnly" /> found the root ineligible, and from the last scan attempt
    ///     when a broker launch or elevation failed. A successful MFT publication clears it. It is the reason the MFT
    ///     was not used, not a record that the producer ran and failed: that is <see cref="RootFailureKind.ProducerFailed" />.
    /// </summary>
    public MftUnavailableReason? MftUnavailable { get; init; }
}
