namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Refuses a drive's watch because a cache-only open adopted its block despite a lost
    ///     journal checkpoint: resuming from that cursor would read a position the journal no
    ///     longer holds. Reported the way any other start failure is, through
    ///     <see cref="DriveStatus.WatchFailureMessage" /> and a faulted
    ///     <see cref="DriveStatus.WatchCatchUp" />, and returned for the start to throw. A
    ///     successful <see cref="RescanAsync" /> writes a fresh cursor, which clears the drive's
    ///     entry in <see cref="_cacheOnlyUnresumableCheckpointOrdinals" /> and this refusal. The
    ///     caller holds <see cref="_stateLock" />.
    /// </summary>
    InvalidOperationException RecordUnresumableCheckpointWatchFailureLocked(DriveRuntime runtime, DriveBlock driveBlock)
    {
        var failure = new InvalidOperationException(
            $"Drive {driveBlock.DriveLetter} cannot be watched: a cache-only open adopted its cached block " +
            "despite a journal checkpoint that could no longer be resumed. Call FileIndex.RescanAsync " +
            "for this drive before watching it.");
        _watchFailureMessagesByOrdinal[driveBlock.DriveOrdinal] = failure.Message;
        runtime.RefusedStartFault = failure;
        return failure;
    }

    /// <summary>The one place a drive's persisted journal cursor is read.</summary>
    static IndexWatchTarget BuildWatchTarget(DriveBlock driveBlock)
    {
        ref readonly var header = ref driveBlock.Block.Header;
        return new IndexWatchTarget(driveBlock.DriveLetter, header.UsnJournalId, header.UsnNextUsn);
    }
}
