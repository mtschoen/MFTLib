namespace MFTLib.Index;

/// <summary>
///     One root's current state. <see cref="Watch" /> carries the live-watch state. The lawful combinations of
///     <see cref="State" />, <see cref="Block" /> and <see cref="Failure" />:
///     <list type="bullet">
///         <item><description><see cref="RootState.Ready" /> with a block and no failure.</description></item>
///         <item><description><see cref="RootState.Ready" /> with a retained block and a <see cref="RootFailureKind.ProducerFailed" /> or <see cref="RootFailureKind.MftSourceMissing" /> failure after a failed rescan or a failed catch-up retry scan during <see cref="FileIndex.OpenAsync" />.</description></item>
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

    /// <summary>Whether the root has a queryable block, is offline, or failed without a block. Capacity exhaustion is reported independently by <see cref="RootBlockStatus.IsCapacityExhausted" />.</summary>
    public RootState State { get; init; }

    /// <summary>
    ///     Why this root failed; null unless it did. Non-null while the root is
    ///     <see cref="RootState.Failed" />, and while a <see cref="RootState.Ready" /> root retains its previous
    ///     block after a failed <see cref="FileIndex.RescanAsync" /> or a failed catch-up
    ///     retry scan during <see cref="FileIndex.OpenAsync" />. A retained-block failure carries
    ///     <see cref="RootFailureKind.ProducerFailed" /> or <see cref="RootFailureKind.MftSourceMissing" />.
    ///     A successful scan publication clears it. A failed rescan of a cache-declined root moves it to
    ///     the actual scan attempt's kind (<see cref="RootFailureKind.ProducerFailed" /> or
    ///     <see cref="RootFailureKind.MftSourceMissing" />), because the scan itself is now what failed.
    ///     An <see cref="RootState.Offline" /> root has none.
    /// </summary>
    public RootFailure? Failure { get; init; }

    /// <summary>The block behind this status; null unless <see cref="State" /> is <see cref="RootState.Ready" />.</summary>
    public RootBlockStatus? Block { get; init; }

    /// <summary>This root's watch capability and retained state, even when no watch is running. Never null.</summary>
    public RootWatchStatus Watch { get; init; } = new();
}
