namespace MFTLib.Index;

/// <summary>
///     One drive's live-watch state within its <see cref="DriveStatus" />, captured with that
///     status. Every value is its default for a drive that is not watched.
/// </summary>
public sealed record DriveWatchStatus
{
    internal DriveWatchStatus()
    {
    }

    /// <summary>True only for an MFT-backed drive with a block whose source offers a watch source; false for a dump or a source without one.</summary>
    public bool Supported { get; init; }

    /// <summary>
    ///     True while this drive's watch is requested: from a
    ///     <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" />, including one whose
    ///     source threw or that was refused over an unresumable block, until
    ///     <see cref="FileIndex.StopWatchingAsync(CancellationToken)" />, a start its own caller
    ///     cancelled, or disposal. A <see cref="FileIndex.RescanAsync(char, CancellationToken)" />
    ///     starts the drive's watch on the replacement block only while this is true; a rescan never
    ///     starts a drive whose watch was not requested. A drive a batched start answered
    ///     <see cref="DriveOperationOutcome.NotApplicable" /> is not requested.
    /// </summary>
    public bool Requested { get; init; }

    /// <summary>
    ///     Where this drive's live watch stands in draining the journal backlog that was present
    ///     when its current watch started. <see cref="WatchCatchUpState.NotStarted" /> while the
    ///     drive's watch is not requested (<see cref="Requested" /> is false) and no start of
    ///     it failed: before its first <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" />
    ///     and again after <see cref="FileIndex.StopWatchingAsync(CancellationToken)" />.
    ///     <see cref="WatchCatchUpState.CatchingUp" /> while the watch applies its backlog,
    ///     <see cref="WatchCatchUpState.CaughtUp" /> once that backlog has been applied and the
    ///     drive is on live entries, and <see cref="WatchCatchUpState.Faulted" /> when the drive's
    ///     watch fails, its start or automatic restart fails, or its start is refused, with detail
    ///     in <see cref="FailureMessage" />: a drive whose automatic recovery failed reads
    ///     <see cref="WatchCatchUpState.Faulted" />, not <see cref="WatchCatchUpState.NotStarted" />,
    ///     although no watch is running. A <see cref="FileIndex.RescanAsync(char, CancellationToken)" />
    ///     of a drive whose watch is requested reads <see cref="WatchCatchUpState.CatchingUp" />
    ///     from the moment it retires the old watch until the replacement catches up. Always
    ///     <see cref="WatchCatchUpState.NotStarted" /> for a drive that cannot be watched at all
    ///     (an enumeration-backed block). A drive a cache-only open adopted despite a lost journal
    ///     checkpoint reads <see cref="WatchCatchUpState.Faulted" /> instead once
    ///     <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> has refused it: it could be watched if its
    ///     cursor were resumable, so this says the watch was refused rather than never attempted.
    ///     The same holds for a drive whose scans lost their journal catch-up
    ///     <see cref="FileIndex.LostCatchUpRecoveryLimit" /> times in a row.
    ///     <see cref="WatchCatchUpState.Recovering" /> while a recovery of the drive is queued or
    ///     running after a <see cref="WatchFaultKind.Drive" /> or <see cref="WatchFaultKind.Apply" />
    ///     fault, or a scan of a watched drive retries after a lost catch-up.
    /// </summary>
    public WatchCatchUpState CatchUpState { get; init; }

    /// <summary>
    ///     How many times this drive's <see cref="CatchUpState" /> has changed since the index
    ///     opened: zero while it has never changed (a cold open whose scans reach the catch-up-loss
    ///     limit opens in <see cref="WatchCatchUpState.Faulted" /> with version 1), and one more
    ///     with every change, each of which <see cref="FileIndex.WatchStateChanged" /> delivers with
    ///     this same number. Read together with <see cref="CatchUpState" /> from one
    ///     <see cref="FileIndex.Drives" /> snapshot, it seeds a subscriber that joins late: an event
    ///     whose <see cref="DriveWatchState.StateVersion" /> is not larger than the one already applied
    ///     for the drive is older news and is dropped. Versions of different drives are
    ///     independent.
    /// </summary>
    public long StateVersion { get; init; }

    /// <summary>
    ///     Set when this drive's live watch failed, from the message of the exception that ended
    ///     it. The drive stays <see cref="DriveState.Ready" />: its block is valid and every query
    ///     still answers from it, but nothing after the failing batch has been applied. Also set
    ///     when the source failed to start the drive's watch. Cleared when the drive's watch is
    ///     started again by <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> or by a rescan. Null while
    ///     the watch is healthy or has never failed.
    ///     <para>
    ///         Also set, without the watch ever having started, for a drive a cache-only open
    ///         adopted despite a lost journal checkpoint: starting it would resume from a cursor
    ///         the journal no longer holds, so <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> refuses
    ///         it and explains why here. Only <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> clears this one,
    ///         since a plain <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> refuses the same drive
    ///         again with the same message.
    ///     </para>
    /// </summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    ///     How many scans of this drive in a row lost their journal catch-up. A scan whose
    ///     catch-up holds sets it back to zero; a scan that produced no block leaves it as it was.
    ///     It spans every scan of the drive for the life of the index, and a scan operation stops
    ///     retrying once it reaches <see cref="FileIndex.LostCatchUpRecoveryLimit" />.
    /// </summary>
    public int ConsecutiveLostCatchUps { get; init; }

    /// <summary>
    ///     Set when this drive's journal position fell out of the journal, so catching the
    ///     drive up is no longer possible and only a full scan can. It carries the position,
    ///     the journal as it stood at that moment, and the size a journal would need to be at
    ///     least to have kept the position.
    ///     <para>
    ///         Three paths fill it in. Two read the live journal unelevated. At open, a cached
    ///         block whose checkpoint is gone is rejected and the drive is
    ///         cold-scanned, unless <see cref="FileIndexOptions.InitialOpenCacheOnly" /> is set,
    ///         in which case the open never scans and adopts the block anyway (it never watches,
    ///         and the block is still a correct snapshot as of its age), leaving this to explain
    ///         the checkpoint alone. Later, a drive's live watch that faults is asked the same
    ///         question about the position it had reached, which is why a watch that dies because
    ///         the journal moved past it reports more than <see cref="FailureMessage" />. In
    ///         that case the drive keeps its block and its rows: nothing after the position has
    ///         been applied, and <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> is what makes it current
    ///         again. The third path reads no journal in the index: a scan whose catch-up the
    ///         broker proved lost publishes its block with the broker's report,
    ///         <see cref="JournalCheckpointLossDetection.ScanCatchUp" />, which replaces any older
    ///         report as the newer fact and is kept by the retries that follow it.
    ///     </para>
    ///     <para>
    ///         A report outlives the moment that produced it, so on its own it is not evidence
    ///         about a fault being handled now. Read
    ///         <see cref="JournalCheckpointLoss.DetectedDuring" /> to tell them apart: inside
    ///         a <see cref="FileIndex.WatchFaulted" /> handler,
    ///         <see cref="JournalCheckpointLossDetection.LiveWatch" /> explains a watch fault, and
    ///         <see cref="JournalCheckpointLossDetection.ScanCatchUp" /> explains a
    ///         <see cref="WatchFaultKind.CatchUpLost" /> fault, the same way: the journal outran
    ///         this drive. A report reading
    ///         <see cref="JournalCheckpointLossDetection.DriveOpening" /> beside a faulted watch
    ///         is the open's standing explanation of a cold scan and says nothing about the
    ///         fault; an unrelated fault neither rewrites nor deletes it.
    ///     </para>
    ///     Null when the drive warm-started, had no cache to resume, was rejected for a reason
    ///     unrelated to the journal, or has never
    ///     lost its position at either moment.
    /// </summary>
    public JournalCheckpointLoss? CheckpointLoss { get; init; }
}
