namespace MFTLib.Index;

/// <summary>The origin of a drive's current block when its status was captured.</summary>
public enum BlockOrigin
{
    /// <summary>Adopted from a valid cache file; its age is given by <see cref="DriveBlockStatus.ScanTimestamp" />.</summary>
    WarmStartedFromCache,

    /// <summary>Produced by this index during a cold open or successful <see cref="FileIndex.RescanAsync" />.</summary>
    ProducedByScan
}
