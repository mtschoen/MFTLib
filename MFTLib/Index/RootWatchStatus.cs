namespace MFTLib.Index;

/// <summary>
///     One root's live-watch state within its <see cref="RootStatus" />, captured with that
///     status. Recovery exhaustion and checkpoint-loss reports can remain when no watch is running.
/// </summary>
public sealed record RootWatchStatus
{
    internal RootWatchStatus()
    {
    }

    /// <summary>True only for an MFT-backed volume with a block whose source offers a watch source; false for a dump or a source without one.</summary>
    public bool IsSupported { get; init; }

    /// <summary>
    ///     True while this root's watch is requested: from a
    ///     <see cref="FileIndex.StartWatchingAsync" />, including one whose
    ///     source threw or that was refused over an unresumable block, until
    ///     <see cref="FileIndex.StopWatchingAsync" />, a start its own caller
    ///     cancelled, or disposal. A <see cref="FileIndex.RescanAsync" />
    ///     starts the root's watch on the replacement block only while this is true; a rescan never
    ///     starts a root whose watch was not requested. A root a batched start answered
    ///     <see cref="RootOperationOutcome.NotApplicable" /> is not requested.
    /// </summary>
    public bool IsRequested { get; init; }

    /// <summary>
    ///     Where this root's live watch stands. <see cref="WatchState.NotStarted" /> while the
    ///     watch is not requested and no start of it failed, and always for a root that cannot be
    ///     watched. <see cref="WatchState.CatchingUp" /> while the watch applies its backlog, or
    ///     while a rescan has retired the old watch and the replacement has not caught up.
    ///     <see cref="WatchState.CaughtUp" /> once the backlog has been applied.
    ///     <see cref="WatchState.Recovering" /> while the index rebuilds a watched root's block.
    ///     <see cref="WatchState.Faulted" /> when the watch failed or was refused, with detail in
    ///     <see cref="FailureMessage" />, including a root whose automatic recovery failed
    ///     (<see cref="IsRecoveryStopped" />) or whose cache-only open adopted a block with a lost
    ///     journal checkpoint that a start then refused.
    /// </summary>
    public WatchState State { get; init; }

    /// <summary>
    ///     How many times this root's <see cref="State" /> has changed since the index
    ///     opened: zero while it has never changed (a cold open whose scans reach the catch-up-loss
    ///     limit opens in <see cref="WatchState.Faulted" /> with version 1), and one more
    ///     with every change, each of which <see cref="FileIndex.WatchStateChanged" /> delivers in a
    ///     <see cref="WatchStateChange.Watch" /> carrying this same number. The
    ///     <see cref="RootStatus.Watch" /> of one <see cref="FileIndex.Status" /> snapshot seeds a
    ///     subscriber that joins late: an event whose <see cref="WatchStateChange.Watch" /> has a
    ///     version not larger than the one already applied for the root is older news and is
    ///     dropped. Versions of different roots are independent.
    /// </summary>
    public long StateVersion { get; init; }

    /// <summary>
    ///     Set when this root's live watch failed, from the message of the exception that ended
    ///     it. The root stays <see cref="RootState.Ready" />: its block is valid and every query
    ///     still answers from it, but nothing after the failing batch has been applied. Also set
    ///     when the source failed to start the root's watch. Cleared when the root's watch is
    ///     started again by <see cref="FileIndex.StartWatchingAsync" /> or by a rescan. Null while
    ///     the watch is healthy or has never failed.
    ///     <para>
    ///         Also set, without the watch ever having started, for a root a cache-only open
    ///         adopted despite a lost journal checkpoint: starting it would resume from a cursor
    ///         the journal no longer holds, so <see cref="FileIndex.StartWatchingAsync" /> refuses
    ///         it and explains why here. Only <see cref="FileIndex.RescanAsync" /> clears this one,
    ///         since a plain <see cref="FileIndex.StartWatchingAsync" /> refuses the same root
    ///         again with the same message.
    ///     </para>
    /// </summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    ///     How many scans of this volume in a row lost their journal catch-up. A scan whose
    ///     catch-up holds sets it back to zero; a scan that produced no block leaves it as it was.
    ///     It spans every scan of the root for the life of the index, and a scan operation stops
    ///     retrying once it reaches <see cref="FileIndex.LostCatchUpRecoveryLimit" />.
    /// </summary>
    internal int ConsecutiveLostCatchUps { get; init; }

    /// <summary>
    ///     True when consecutive scan catch-up losses exhausted automatic recovery for this root.
    ///     Published before the loss notification or open completion. Failed or cancelled scans
    ///     and stopping the watch preserve it; publishing a successful catch-up clears it.
    ///     Call <see cref="FileIndex.RescanAsync" /> to restore a resumable block.
    /// </summary>
    public bool IsRecoveryStopped { get; init; }

    /// <summary>
    ///     Set when this volume's journal position fell out of the journal, so catching the
    ///     root up is no longer possible and only a full scan can. It carries the position,
    ///     the journal as it stood at that moment, and the size a journal would need to be at
    ///     least to have kept the position.
    ///     <para>
    ///         Three paths fill it in. Two read the live journal unelevated. At open, a cached
    ///         block whose checkpoint is gone is rejected and the root is
    ///         cold-scanned, unless <see cref="CacheMode.ExistingOnly" /> is set,
    ///         in which case the open never scans and adopts the block anyway (it never watches,
    ///         and the block is still a correct snapshot as of its age), leaving this to explain
    ///         the checkpoint alone. Later, a root's live watch that faults is asked the same
    ///         question about the position it had reached, which is why a watch that dies because
    ///         the journal moved past it reports more than <see cref="FailureMessage" />. In
    ///         that case the root keeps its block and its rows, with its cursor at the lost
    ///         position. A <c>WatchFaultKind.Volume</c> read failure applied nothing past it; an
    ///         <c>WatchFaultKind.Apply</c> fault may have left part of its last batch applied ahead
    ///         of the cursor. <see cref="FileIndex.RescanAsync" /> makes it current
    ///         again. The third path reads no journal in the index: a scan whose catch-up the
    ///         broker proved lost publishes its block with the broker's report,
    ///         <see cref="JournalCheckpointLossDetection.ScanCatchUp" />, which replaces any older
    ///         report as the newer fact and is kept by the retries that follow it.
    ///     </para>
    ///     <para>
    ///         A report outlives the moment that produced it, so on its own it is not evidence
    ///         about a fault being handled now. Read
    ///         <see cref="JournalCheckpointLoss.DetectedDuring" /> to learn which moment found it:
    ///         <see cref="JournalCheckpointLossDetection.ScanCatchUp" /> is recorded before the
    ///         <see cref="WatchFaultKind.CatchUpLost" /> fault it explains is announced. A report
    ///         reading <see cref="JournalCheckpointLossDetection.RootOpening" /> is the open's
    ///         standing explanation of a cold scan, and one reading
    ///         <see cref="JournalCheckpointLossDetection.LiveWatch" /> survives the automatic
    ///         recovery of the fault that found it. Either can sit beside a later, unrelated
    ///         fault, which neither rewrites nor deletes it.
    ///     </para>
    ///     Null when the volume warm-started with its journal position intact, had no cache to resume,
    ///     was rejected for a reason unrelated to the journal, or has never lost its position at either
    ///     moment. A cache-only open that adopts a block whose position the journal moved past keeps
    ///     that block and carries the report that made it unresumable.
    /// </summary>
    public JournalCheckpointLoss? CheckpointLoss { get; init; }
}
