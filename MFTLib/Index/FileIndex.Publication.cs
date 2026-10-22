namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Swaps in a new snapshot over the current runtime blocks, which the
    ///     caller has just committed. The caller holds <see cref="_stateLock" />, which is what
    ///     orders every publication and every change to <see cref="DriveRuntime._retiredBlocks" />.
    /// </summary>
    void PublishSnapshotLocked()
    {
        ObjectDisposedException.ThrowIf(_snapshot is null, this);
        _snapshot = [.. _driveRuntimes.Values
            .Select(runtime => runtime.Block)
            .OfType<DriveBlock>()
            .OrderBy(block => block.DriveOrdinal)];
    }
}
