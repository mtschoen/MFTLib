namespace MFTLib.Index;

/// <summary>Identifies which boundary raised a live watch fault.</summary>
public enum WatchFaultKind
{
    /// <summary>
    ///     A <see cref="FileIndex.Changed" /> subscriber threw while receiving a batch. The root
    ///     keeps watching; this is announced once per watch of the root.
    /// </summary>
    Subscriber,

    /// <summary>
    ///     The root's own watch failed (its read threw the watch's root failure). The root's watch has ended and its recovery has started: the root reads
    ///     <see cref="WatchState.Recovering" /> while the index rescans it and then starts
    ///     its watch again from the new block's cursor.
    /// </summary>
    Volume,

    /// <summary>
    ///     The index could not apply a batch. Entries applied before the failure can remain in
    ///     the block while the root's cursor stays at its previous position, so the block is not
    ///     trusted: the root's watch has ended and its recovery has started, as for
    ///     <see cref="Volume" />, and the rescan replaces the block.
    /// </summary>
    Apply,

    /// <summary>
    ///     A scan of the volume lost its journal catch-up: the journal proved that the cursor armed
    ///     before the scan could no longer be read when the scan finished. The exception is a
    ///     <see cref="JournalCatchUpLostException" />. The scan's block is published but cannot be
    ///     watched from its cursor. Raised by the scan operation, which rescans the root at once
    ///     unless <see cref="JournalCatchUpLostException.IsRecoveryStopped" /> says automatic recovery
    ///     from consecutive scan catch-up losses has stopped.
    /// </summary>
    CatchUpLost,

    /// <summary>
    ///     The channel carrying the volume's watch was lost, or the watch ended without being
    ///     stopped. The root's watch has ended and no recovery starts.
    /// </summary>
    Channel,

    /// <summary>
    ///     The recovery of a faulted root did not restore its watch: its scan or its restart
    ///     failed, or the restarted watch failed before it first caught up (the exception is then
    ///     that watch's own failure). The root stays <see cref="WatchState.Faulted" />, with
    ///     no further automatic recovery, until <see cref="FileIndex.RescanAsync" /> or
    ///     <see cref="FileIndex.StartWatchingAsync" /> is called for it.
    /// </summary>
    Recovery,

    /// <summary>
    ///     A rescan replaced the block but could not start its watch. The exception describes the
    ///     rescan and carries the start failure as its inner exception. The root stays faulted
    ///     until a consumer starts or rescans it; no automatic recovery is queued. Stop returns
    ///     this fault once as a Failed result.
    /// </summary>
    RescanRestart
}

/// <summary>
///     A fault observed by one root's watch pump, or by a scan that lost its catch-up or could
///     not start its replacement watch. Every fault names its root.
/// </summary>
public sealed record WatchFault
{
    /// <summary>Creates a fault report.</summary>
    /// <param name="kind">Which boundary raised the fault.</param>
    /// <param name="volume">The root the fault names.</param>
    /// <param name="exception">The exception that ended or disturbed the watch.</param>
    internal WatchFault(WatchFaultKind kind, VolumeKey volume, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Kind = kind;
        Volume = volume;
        Exception = exception;
    }

    /// <summary>Which boundary raised the fault.</summary>
    public WatchFaultKind Kind { get; init; }

    /// <summary>The root whose watch or scan faulted; a fault always names one root.</summary>
    public VolumeKey Volume { get; init => field = value.Validated(); }

    /// <summary>The failure itself; its type depends on <see cref="Kind" />, for example a <see cref="JournalCatchUpLostException" /> for a lost catch-up.</summary>
    public Exception Exception { get; init; }

    /// <summary>
    ///     Whether automatic recovery accompanies this notification: a root or apply fault, or
    ///     a scan catch-up loss whose <see cref="JournalCatchUpLostException.IsRecoveryStopped" /> is false.
    ///     This classifies the reported fault, not the root's subsequent live-watch health.
    /// </summary>
    public bool IsRecovering =>
        Kind is WatchFaultKind.Volume or WatchFaultKind.Apply ||
        (Kind == WatchFaultKind.CatchUpLost &&
        Exception is JournalCatchUpLostException { IsRecoveryStopped: false });
}
