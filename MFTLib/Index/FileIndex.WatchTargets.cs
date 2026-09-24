namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>One MFT-backed drive left out of a watch because its cursor cannot be resumed.</summary>
    readonly record struct UnresumableWatchDrive(char DriveLetter, ushort DriveOrdinal);

    /// <summary>
    ///     Every MFT-backed drive's watch target, split from the drives
    ///     <see cref="_cacheOnlyUnresumableCheckpointOrdinals" /> marks unresumable: arming a
    ///     watch from one of those would resume from a journal cursor the journal no longer
    ///     holds, so they are reported separately instead of silently joining the stream. For a
    ///     rescan-triggered restart, any drive other than <paramref name="rescannedDriveLetter" />
    ///     that still has a recorded watch failure is left out as well.
    /// </summary>
    (List<IndexWatchTarget> Targets, List<UnresumableWatchDrive> Unresumable) BuildWatchTargets(
        char? rescannedDriveLetter)
    {
        lock (_stateLock)
        {
            var targets = new List<IndexWatchTarget>(_driveBlocks.Count);
            List<UnresumableWatchDrive>? unresumable = null;
            foreach (var driveBlock in _driveBlocks)
            {
                if (driveBlock.ProducerKind != ProducerKind.Mft)
                {
                    continue;
                }

                if (_cacheOnlyUnresumableCheckpointOrdinals.Contains(driveBlock.DriveOrdinal))
                {
                    (unresumable ??= []).Add(
                        new UnresumableWatchDrive(driveBlock.DriveLetter, driveBlock.DriveOrdinal));
                    continue;
                }

                if (rescannedDriveLetter is { } rescanned &&
                    char.ToUpperInvariant(driveBlock.DriveLetter) != char.ToUpperInvariant(rescanned) &&
                    _watchFailureMessagesByOrdinal.ContainsKey(driveBlock.DriveOrdinal))
                {
                    continue;
                }

                targets.Add(BuildWatchTarget(driveBlock));
            }

            return (targets, unresumable ?? []);
        }
    }

    /// <summary>
    ///     Marks one drive's watch as unarmable because a cache-only open adopted its block
    ///     despite a lost journal checkpoint: resuming from that cursor would read a position the
    ///     journal no longer holds. Reported the same way any other watch failure is, so a
    ///     consumer sees one explanation rather than a silently short target list. A successful
    ///     <see cref="RescanAsync" /> writes a fresh cursor, which clears this drive's entry in
    ///     <see cref="_cacheOnlyUnresumableCheckpointOrdinals" /> and, through the same paths an
    ///     ordinary watch-fault recovery uses, this failure message and faulted catch-up slot.
    ///     The caller holds <see cref="_stateLock" />.
    /// </summary>
    void RecordUnresumableCheckpointWatchFailureLocked(char driveLetter, ushort driveOrdinal)
    {
        var failure = new InvalidOperationException(
            $"Drive {driveLetter} cannot be watched: a cache-only open adopted its cached block " +
            "despite a journal checkpoint that could no longer be resumed. Call FileIndex.RescanAsync " +
            "for this drive before watching it.");
        _watchFailureMessagesByOrdinal[driveOrdinal] = failure.Message;
        FaultWatchCatchUpLocked(driveOrdinal, failure);
    }

    /// <summary>One drive's counterpart to <see cref="BuildWatchTargets" />, for a re-arm.</summary>
    IndexWatchTarget BuildWatchTarget(char driveLetter)
    {
        lock (_stateLock)
        {
            foreach (var driveBlock in _driveBlocks)
            {
                if (char.ToUpperInvariant(driveBlock.DriveLetter) == char.ToUpperInvariant(driveLetter))
                {
                    return BuildWatchTarget(driveBlock);
                }
            }
        }

        throw new InvalidOperationException($"Drive {driveLetter} has no block to resume a watch from.");
    }

    /// <summary>The one place a drive's persisted journal cursor is read.</summary>
    static IndexWatchTarget BuildWatchTarget(DriveBlock driveBlock)
    {
        ref readonly var header = ref driveBlock.Block.Header;
        return new IndexWatchTarget(driveBlock.DriveLetter, header.UsnJournalId, header.UsnNextUsn);
    }
}
