namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Asks the live journal whether a drive whose watch just died is still inside it, and
    ///     records a <see cref="JournalCheckpointLoss" /> when it is not. The position asked
    ///     about is the drive's block header cursor, which is both where
    ///     <see cref="BuildWatchTarget(DriveBlock)" /> armed the watch from and where every
    ///     applied batch has since advanced it to, so this is the same question over the same
    ///     numbers that <see cref="RejectUnresumableCheckpoint" /> asks of a cached block at
    ///     open. Running it on every watch fault rather than on a classified subset of them is
    ///     deliberate: the native layer turns a journal error into a message and no error code
    ///     reaches managed code, so the only honest classifier is the journal itself. A fault
    ///     with an unrelated cause records nothing, because the position is still there, not
    ///     because the exception was inspected.
    /// </summary>
    /// <remarks>
    ///     The journal read runs outside <see cref="_stateLock" />, and what it answers is a
    ///     point-in-time fact about a journal that keeps moving, which is all it claims to be.
    ///     The loss is recorded only while <paramref name="instance" /> is still the drive's
    ///     current watch and the block it was armed from is still this drive's block, so a rescan
    ///     that swapped the block in the meantime, and cleared the reports that explained the old
    ///     one with it, is not undone by this, and neither is a newer watch.
    ///     <para>
    ///         What this deliberately does not do is clear a report it did not produce. A drive
    ///         can already carry one from <see cref="JournalCheckpointLossDetection.DriveOpening" />,
    ///         which explains why this session cold-scanned and which an unrelated fault does
    ///         not falsify. A loss found here replaces it, as the newer fact about the same
    ///         drive; anything else leaves it alone, and
    ///         <see cref="JournalCheckpointLoss.DetectedDuring" /> is what keeps the two
    ///         distinguishable to a <see cref="WatchFaulted" /> handler.
    ///     </para>
    /// </remarks>
    void RecordCheckpointLossForFaultedDrive(DriveRuntime runtime, WatchInstance instance)
    {
        var driveLetter = runtime.DriveLetter;
        var driveBlock = instance.ArmedBlock;
        ulong journalId;
        long watchPositionUsn;
        lock (_stateLock)
        {
            if (!IsCurrentWatchOverItsBlockLocked(runtime, instance))
            {
                return;
            }

            ref readonly var header = ref driveBlock.Block.Header;
            journalId = header.UsnJournalId;
            watchPositionUsn = header.UsnNextUsn;
        }

        if (JournalCheckpointCheck.Check(driveLetter, journalId, watchPositionUsn,
                JournalCheckpointLossDetection.LiveWatch) is not { } loss)
        {
            // The position is still in the journal, so this fault was not the journal's doing.
            // A report already on this drive describes a different moment, and this fault did
            // not make it untrue, so it stays: it explains the block that is still in place,
            // and it says which moment it came from, so a fault handler reading it does not
            // mistake it for this one. Erasing it here would destroy a fact instead.
            return;
        }

        lock (_stateLock)
        {
            if (IsCurrentWatchOverItsBlockLocked(runtime, instance))
            {
                _checkpointLossesByOrdinal[driveBlock.DriveOrdinal] = loss;
            }
        }
    }

    /// <summary>
    ///     The scoping rule's check: <paramref name="instance" /> is still its drive's current
    ///     watch and the block it was armed from is still the drive's published block. The caller
    ///     holds <see cref="_stateLock" />.
    /// </summary>
    bool IsCurrentWatchOverItsBlockLocked(DriveRuntime runtime, WatchInstance instance)
    {
        return ReferenceEquals(runtime.Current, instance) &&
               ReferenceEquals(FindWatchableDriveBlockLocked(runtime.DriveLetter), instance.ArmedBlock);
    }

    /// <summary>
    ///     This drive's MFT-backed block, or null when it has none. The caller holds
    ///     <see cref="_stateLock" /> and compares the instance, not the ordinal, so a block a
    ///     rescan replaced is recognizable as a different one.
    /// </summary>
    DriveBlock? FindWatchableDriveBlockLocked(char driveLetter)
    {
        var upperLetter = char.ToUpperInvariant(driveLetter);
        foreach (var driveBlock in _driveBlocks)
        {
            if (char.ToUpperInvariant(driveBlock.DriveLetter) == upperLetter &&
                driveBlock.ProducerKind == ProducerKind.Mft && !driveBlock.IsMftDump)
            {
                return driveBlock;
            }
        }

        return null;
    }

    /// <summary>
    ///     Refuses a drive's watch because its block's journal cursor cannot be resumed: a
    ///     cache-only open adopted the block despite a lost checkpoint, or the scan that produced it
    ///     lost its journal catch-up. Resuming from that cursor would read a position the journal no
    ///     longer holds. Reported the way any other start failure is, through
    ///     <see cref="DriveWatchStatus.FailureMessage" /> and a faulted
    ///     <see cref="DriveWatchStatus.CatchUpState" />, and returned for the start to throw. Like a
    ///     start whose source threw, the refusal records the watch as requested and faults any
    ///     restart-pending wait. A rescan whose catch-up holds writes a fresh cursor, which clears
    ///     the drive's entry in <see cref="_unresumableCheckpointsByOrdinal" /> and this refusal,
    ///     and then starts the requested watch. The caller holds <see cref="_stateLock" />.
    /// </summary>
    InvalidOperationException RecordUnresumableCheckpointWatchFailureLocked(DriveRuntime runtime,
        DriveBlock driveBlock, UnresumableCheckpointReason reason)
    {
        var because = reason == UnresumableCheckpointReason.LostCatchUp
            ? $"{runtime.ConsecutiveLostCatchUps} scans in a row lost their journal catch-up, so its block's " +
              "journal cursor could no longer be resumed."
            : "a cache-only open adopted its cached block despite a journal checkpoint that could no longer " +
              "be resumed.";
        var failure = new InvalidOperationException(
            $"Drive {driveBlock.DriveLetter} cannot be watched: {because} Call FileIndex.RescanAsync " +
            "for this drive before watching it.");
        _watchFailureMessagesByOrdinal[driveBlock.DriveOrdinal] = failure.Message;
        runtime.WatchRequested = true;
        runtime.RefusedStartFault = failure;
        FaultRestartPendingWaiterLocked(runtime, failure);
        return failure;
    }

    /// <summary>
    ///     The failure of a start over a drive with no MFT-backed block. A restart whose rescan
    ///     replaced the block with one that cannot be watched withdraws the watch request, since
    ///     the drive can no longer be watched at all, and faults any restart-pending wait. A
    ///     faulted watch the rescan retained is superseded with its status, so the drive reads
    ///     <see cref="WatchCatchUpState.NotStarted" /> whether its old watch was healthy or
    ///     faulted. That fault was already raised through <see cref="WatchFaulted" />, and no stop
    ///     remains to rethrow it. The caller holds <see cref="_stateLock" />.
    /// </summary>
    InvalidOperationException RefuseStartWithoutWatchableBlockLocked(DriveRuntime runtime, bool restart)
    {
        var failure = new InvalidOperationException(
            $"Drive {runtime.DriveLetter} has no MFT-backed block, so there is no journal cursor to watch from.");
        if (!restart)
        {
            return failure;
        }

        runtime.WatchRequested = false;
        runtime.RefusedStartFault = null;
        runtime.RescanHandoffFault = null;
        FaultRestartPendingWaiterLocked(runtime, failure);
        if (RetireCurrentLocked(runtime) is { } superseded)
        {
            superseded.OutstandingFault = null;
        }

        if (TryGetDriveOrdinalLocked(runtime.DriveLetter, out var driveOrdinal))
        {
            _watchFailureMessagesByOrdinal.Remove(driveOrdinal);
        }

        return failure;
    }

    /// <summary>The watch target at the journal cursor persisted in the drive's block header.</summary>
    static IndexWatchTarget BuildWatchTarget(DriveBlock driveBlock)
    {
        ref readonly var header = ref driveBlock.Block.Header;
        return new IndexWatchTarget(driveBlock.DriveLetter, header.UsnJournalId, header.UsnNextUsn);
    }
}
