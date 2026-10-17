namespace MFTLib.Index;

public sealed partial class FileIndex
{
    DriveStatus DescribeDriveLocked(DriveRuntime runtime)
    {
        if (runtime.Block is not { } driveBlock)
        {
            return new DriveStatus(runtime.Drive, runtime.BlocklessState)
            {
                Failure = runtime.Failure,
                Watch = new DriveWatchStatus { CheckpointLoss = runtime.CheckpointLoss }
            };
        }

        var result = runtime.LastResult ?? throw new InvalidOperationException(
            $"Drive {runtime.Drive} has a block without its adopted result.");
        ref readonly var header = ref driveBlock.Block.Header;
        return new DriveStatus(runtime.Drive, DriveState.Ready)
        {
            Failure = runtime.Failure,
            Block = new DriveBlockStatus
            {
                Origin = result.Origin,
                CacheLocation = result.CacheLocation,
                LiveRowCount = header.LiveRowCount,
                ScanTimestamp = header.ScanTimestampUtc,
                IsCapacityExhausted = header.IsCapacityExhausted,
                SkippedRecordCount = result.SkippedRecordCount,
                AccessDeniedSubtreeCount = result.AccessDeniedSubtreeCount
            },
            Watch = DescribeWatchLocked(runtime, driveBlock)
        };
    }
}
