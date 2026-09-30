namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Refuses a drive's watch because its block's journal cursor cannot be resumed: a
    ///     cache-only open adopted the block despite a lost checkpoint, or the scan that produced it
    ///     lost its journal catch-up. Resuming from that cursor would read a position the journal no
    ///     longer holds. Reported the way any other start failure is, through
    ///     <see cref="DriveStatus.WatchFailureMessage" /> and a faulted
    ///     <see cref="DriveStatus.WatchCatchUp" />, and returned for the start to throw. A rescan
    ///     whose catch-up holds writes a fresh cursor, which clears the drive's entry in
    ///     <see cref="_unresumableCheckpointsByOrdinal" /> and this refusal. The caller holds
    ///     <see cref="_stateLock" />.
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
