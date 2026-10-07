namespace MFTLib.Index;

/// <summary>
///     One drive's current state. Online status includes block-header data; blockless status
///     carries a zero live row count.
/// </summary>
public sealed record DriveStatus
{
    /// <summary>Creates a status with the values every drive has, whether or not it has a block.</summary>
    /// <param name="driveLetter">The drive.</param>
    /// <param name="state">Whether the drive can answer queries, needs a rescan, or has no block.</param>
    /// <param name="blockSource">Where the block behind this status came from.</param>
    /// <param name="liveRowCount">Rows a query can return; zero for a drive with no block.</param>
    /// <param name="compactionNeeded">The block header's compaction flag.</param>
    /// <param name="scanTimestamp">When the current block's production completed.</param>
    internal DriveStatus(char driveLetter, DriveState state, BlockSource blockSource, uint liveRowCount,
        bool compactionNeeded, DateTime scanTimestamp)
    {
        DriveLetter = driveLetter;
        State = state;
        BlockSource = blockSource;
        LiveRowCount = liveRowCount;
        CompactionNeeded = compactionNeeded;
        ScanTimestamp = scanTimestamp;
    }

    /// <summary>Identifies the drive in every per-drive <see cref="FileIndex" /> call; one status exists per configured drive, including failed and offline ones.</summary>
    public char DriveLetter { get; init; }

    /// <summary>
    ///     Where the block behind this status came from. <see cref="BlockSource.None" /> for a
    ///     drive with no block. Reads <see cref="BlockSource.ProducedByScan" /> after a successful
    ///     rescan, because that rescan is what produced the current block.
    /// </summary>
    public BlockSource BlockSource { get; init; }

    /// <summary>
    ///     The current block's cache-slot backing, captured with this status. Read
    ///     <see cref="FileIndex.Drives" /> again after a rescan for an updated value.
    ///     <see cref="CacheSlotState.NotApplicable" /> for NoCache or blockless drives.
    ///     This does not identify another owner or promise that a private block's slot
    ///     is still held elsewhere. <see cref="BlockSource" /> independently describes
    ///     how the current block was obtained.
    /// </summary>
    public CacheSlotState CacheSlot { get; init; }

    /// <summary>Whether the drive can answer queries, needs a rescan, or has no block.</summary>
    public DriveState State { get; init; }

    /// <summary>
    ///     Rows that are in use and not tombstoned: the rows a query can return. Zero for a
    ///     drive with no block.
    /// </summary>
    public uint LiveRowCount { get; init; }

    /// <summary>
    ///     UTC time at which the current block's production completed, stamped into its header.
    ///     A block adopted from the cache keeps its original time, so this is the block's age,
    ///     not the time the index opened. <see cref="DateTime.MinValue" /> for a drive with no block.
    /// </summary>
    public DateTime ScanTimestamp { get; init; }

    /// <summary>
    ///     The block header's compaction flag: a mutation or a producer write could not fit, so
    ///     the block is incomplete or stale and only a rescan repairs it.
    /// </summary>
    public bool CompactionNeeded { get; init; }

    /// <summary>True only for an MFT-backed drive with a block whose source offers a watch source; false for a dump or a source without one.</summary>
    public bool WatchSupported { get; init; }

    /// <summary>
    ///     The enumeration producer's count of subtrees it could not enter during the latest
    ///     scan, including directories that vanished before entry. Zero for an MFT-backed
    ///     drive and after a warm start, because the count is not stored in the block.
    /// </summary>
    public int AccessDeniedSubtreeCount { get; init; }

    /// <summary>
    ///     The MFT producer's count of records it could not place during the latest scan:
    ///     unsupported record identifiers, empty names and exhausted block capacity. Zero for
    ///     an enumeration-backed drive and after a warm start, because the count is not stored
    ///     in the block.
    /// </summary>
    public int SkippedRecordCount { get; init; }

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
    ///     <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> of a failed drive clears this back to
    ///     <see cref="DriveFailureKind.None" /> along with the state; a failed rescan of a
    ///     cache-declined drive moves it to <see cref="DriveFailureKind.ProducerFailed" />,
    ///     because the scan itself is now what failed.
    /// </summary>
    public DriveFailureKind FailureKind { get; init; }

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
    public string? WatchFailureMessage { get; init; }

    /// <summary>
    ///     Where this drive's live watch stands in draining the journal backlog that was present
    ///     when its current watch started. <see cref="WatchCatchUpState.NotStarted" /> while the
    ///     drive's watch is not requested (<see cref="WatchRequested" /> is false) and no start of
    ///     it failed: before its first <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" />
    ///     and again after <see cref="FileIndex.StopWatchingAsync(CancellationToken)" />.
    ///     <see cref="WatchCatchUpState.CatchingUp" /> while the watch applies its backlog,
    ///     <see cref="WatchCatchUpState.CaughtUp" /> once that backlog has been applied and the
    ///     drive is on live entries, and <see cref="WatchCatchUpState.Faulted" /> when the drive's
    ///     watch fails, its start or automatic restart fails, or its start is refused, with detail
    ///     in <see cref="WatchFailureMessage" />: a drive whose automatic recovery failed reads
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
    public WatchCatchUpState WatchCatchUpState { get; init; }

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
    public bool WatchRequested { get; init; }

    /// <summary>
    ///     How many times this drive's <see cref="WatchCatchUpState" /> has changed since the index
    ///     opened: zero while it has never changed (a cold open whose scans reach the catch-up-loss
    ///     limit opens in <see cref="WatchCatchUpState.Faulted" /> with version 1), and one more
    ///     with every change, each of which <see cref="FileIndex.WatchStateChanged" /> delivers with
    ///     this same number. Read together with <see cref="WatchCatchUpState" /> from one
    ///     <see cref="FileIndex.Drives" /> snapshot, it seeds a subscriber that joins late: an event
    ///     whose <see cref="DriveWatchState.WatchStateVersion" /> is not larger than the one already applied
    ///     for the drive is older news and is dropped. Versions of different drives are
    ///     independent.
    /// </summary>
    public long WatchStateVersion { get; init; }

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
    ///         the journal moved past it reports more than <see cref="WatchFailureMessage" />. In
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
