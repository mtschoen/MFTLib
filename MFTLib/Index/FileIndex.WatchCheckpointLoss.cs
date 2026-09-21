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
    ///     The loss is recorded only while the block whose cursor it describes is still this
    ///     drive's block, so a rescan that swapped the block in the meantime, and cleared the
    ///     reports that explained the old one with it, is not undone by this.
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
    void RecordCheckpointLossForFaultedDrive(char driveLetter)
    {
        ulong journalId;
        long watchPositionUsn;
        DriveBlock driveBlock;
        lock (_stateLock)
        {
            if (FindWatchableDriveBlockLocked(driveLetter) is not { } watched)
            {
                // A letter with no block of its own, or one an enumeration producer filled, has
                // no journal cursor and so nothing that can have fallen out of a journal.
                return;
            }

            driveBlock = watched;
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
            if (ReferenceEquals(FindWatchableDriveBlockLocked(driveLetter), driveBlock))
            {
                _checkpointLossesByOrdinal[driveBlock.DriveOrdinal] = loss;
            }
        }
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
                driveBlock.ProducerKind == ProducerKind.Mft)
            {
                return driveBlock;
            }
        }

        return null;
    }
}
