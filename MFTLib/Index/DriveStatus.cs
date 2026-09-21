namespace MFTLib.Index;

/// <summary>
///     One drive's current state. Online status includes block-header data; blockless status
///     carries zero row counts.
/// </summary>
public sealed record DriveStatus
{
    public required char DriveLetter { get; init; }

    public required ProducerKind ProducerKind { get; init; }

    /// <summary>
    ///     Where the block behind this status came from. <see cref="BlockSource.None" /> for a
    ///     drive with no block. Reads <see cref="BlockSource.ProducedByScan" /> after a successful
    ///     rescan, because that rescan is what produced the current block.
    /// </summary>
    public required BlockSource BlockSource { get; init; }

    public required DriveState State { get; init; }

    public required uint RowCount { get; init; }

    /// <summary>
    ///     Rows that are in use and not tombstoned. <see cref="RowCount" /> is the highest used
    ///     slot plus one and can include free slots and deleted files.
    /// </summary>
    public required uint LiveRowCount { get; init; }

    public required DateTime ScanTimestamp { get; init; }

    public required bool CompactionNeeded { get; init; }

    /// <summary>True only for an MFT-backed drive with a block and a live-watch cursor.</summary>
    public required bool WatchSupported { get; init; }

    /// <summary>How many subtrees the producer skipped because it was denied access.</summary>
    public int AccessDeniedSubtreeCount { get; init; }

    /// <summary>
    ///     Set when opening this drive found an existing block at its cache path, rejected it,
    ///     and cold-scanned instead. Null when the current block came from a warm start or a
    ///     first-ever scan with nothing to reject.
    /// </summary>
    public BlockValidationResult? DiscardedBlock { get; init; }

    /// <summary>
    ///     The detail behind <see cref="FailureKind" />: the MFT producer's error when the
    ///     producer failed during opening or its latest rescan, or the cache-only refusal when
    ///     <see cref="FileIndexOptions.InitialOpenCacheOnly" /> declined the drive. A drive that
    ///     fails during opening has <see cref="DriveState.Failed" /> and no block. A failed
    ///     rescan leaves the previous block in place. Null after a successful MFT production,
    ///     when enumeration was selected explicitly, or on a warm start.
    /// </summary>
    public string? MftProducerFailureMessage { get; init; }

    /// <summary>
    ///     Why this drive is <see cref="DriveState.Failed" />:
    ///     <see cref="DriveFailureKind.CacheDeclined" /> when a cache-only open declined it for
    ///     lack of a usable cache block, <see cref="DriveFailureKind.InUse" /> when the cache
    ///     block is locked by another live index, <see cref="DriveFailureKind.ProducerFailed" />
    ///     when its MFT producer failed. <see cref="DriveFailureKind.None" /> in every other
    ///     state, including <see cref="DriveState.Offline" />. A successful
    ///     <see cref="FileIndex.RescanAsync" /> of a failed drive clears this back to
    ///     <see cref="DriveFailureKind.None" /> along with the state; a failed rescan of a
    ///     cache-declined drive moves it to <see cref="DriveFailureKind.ProducerFailed" />,
    ///     because the scan itself is now what failed.
    /// </summary>
    public DriveFailureKind FailureKind { get; init; }

    /// <summary>
    ///     Set when this drive's live watch failed, from the message of the exception that ended
    ///     it. The drive stays <see cref="DriveState.Ready" />: its block is valid and every query
    ///     still answers from it, but nothing after the failing batch has been applied. Cleared
    ///     when the drive is armed again by <see cref="FileIndex.StartWatchingAsync" /> or by a
    ///     rescan. Null while the watch is healthy or not running.
    ///     <para>
    ///         Also set, without the watch ever having started, for a drive a cache-only open
    ///         adopted despite a lost journal checkpoint: arming it would resume from a cursor the
    ///         journal no longer holds, so <see cref="FileIndex.StartWatchingAsync" /> leaves it
    ///         out of the watch and explains why here instead. Only <see cref="FileIndex.RescanAsync" />
    ///         clears this one, since a plain <see cref="FileIndex.StartWatchingAsync" /> leaves the
    ///         same drive out again with the same message.
    ///     </para>
    /// </summary>
    public string? WatchFailureMessage { get; init; }

    /// <summary>
    ///     Where this drive's live watch stands in draining the journal backlog that was present
    ///     when its current arm started. Session-scoped: <see cref="WatchCatchUpState.NotStarted" />
    ///     whenever no session is draining the drive (before the first
    ///     <see cref="FileIndex.StartWatchingAsync" /> and again after
    ///     <see cref="FileIndex.StopWatchingAsync" />), <see cref="WatchCatchUpState.CatchingUp" />
    ///     while an arm applies its backlog, <see cref="WatchCatchUpState.CaughtUp" /> once that
    ///     backlog has been applied and the drive is on live entries, and
    ///     <see cref="WatchCatchUpState.Faulted" /> when the drive's watch fails, with detail in
    ///     <see cref="WatchFailureMessage" />. <see cref="FileIndex.RescanAsync" /> resets the one
    ///     drive it re-arms to <see cref="WatchCatchUpState.CatchingUp" />. Always
    ///     <see cref="WatchCatchUpState.NotStarted" /> for a drive that cannot be watched at all
    ///     (an enumeration-backed block). A drive a cache-only open adopted despite a lost journal
    ///     checkpoint reads <see cref="WatchCatchUpState.Faulted" /> instead, the moment
    ///     <see cref="FileIndex.StartWatchingAsync" /> is first called and leaves it out: it could
    ///     be watched if its cursor were resumable, so this says the watch was refused rather than
    ///     never attempted.
    /// </summary>
    public WatchCatchUpState WatchCatchUp { get; init; }

    /// <summary>
    ///     Set when this drive's journal position fell out of the journal, so catching the
    ///     drive up is no longer possible and only a full scan can. It carries the position,
    ///     the journal as it stood at that moment, and the size a journal would need to be at
    ///     least to have kept the position.
    ///     <para>
    ///         Two paths fill it in, both from the same unelevated read of the live journal.
    ///         At open, a cached block whose checkpoint is gone is rejected and the drive is
    ///         cold-scanned, unless <see cref="FileIndexOptions.InitialOpenCacheOnly" /> is set,
    ///         in which case the open never scans and adopts the block anyway (it never watches,
    ///         and the block is still a correct snapshot as of its age), leaving this to explain
    ///         the checkpoint alone. Mid-session, a live watch that faults is asked the same
    ///         question about the position it had reached, which is why a watch that dies because
    ///         the journal moved past it reports more than <see cref="WatchFailureMessage" />. In
    ///         that case the drive keeps its block and its rows: nothing after the position has
    ///         been applied, and <see cref="FileIndex.RescanAsync" /> is what makes it current
    ///         again.
    ///     </para>
    ///     <para>
    ///         A report outlives the moment that produced it, so on its own it is not evidence
    ///         about a fault being handled now. Read
    ///         <see cref="JournalCheckpointLoss.DetectedDuring" /> to tell the two apart: inside
    ///         a <see cref="FileIndex.WatchFaulted" /> handler, only
    ///         <see cref="JournalCheckpointLossDetection.LiveWatch" /> means the journal outran
    ///         this drive. A report reading
    ///         <see cref="JournalCheckpointLossDetection.DriveOpening" /> beside a faulted watch
    ///         is the open's standing explanation of a cold scan and says nothing about the
    ///         fault; an unrelated fault neither rewrites nor deletes it.
    ///     </para>
    ///     Null when the drive warm-started, had no cache to resume, was rejected for a reason
    ///     unrelated to the journal (which <see cref="DiscardedBlock" /> covers), or has never
    ///     lost its position at either moment.
    /// </summary>
    public JournalCheckpointLoss? CheckpointLoss { get; init; }
}
