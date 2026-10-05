using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Reads what an open <see cref="FileIndex" /> holds for a drive, beyond what its status reports.</summary>
public static class SyntheticIndexInspection
{
    /// <summary>
    ///     Reads the header of the drive's current block under a snapshot borrow, so a concurrent
    ///     rescan or disposal waits for the read instead of unmapping the block under it.
    /// </summary>
    /// <param name="index">The open index.</param>
    /// <param name="driveLetter">The drive whose block header is read.</param>
    /// <returns>The header values, or null when the drive has no block (offline, declined or failed).</returns>
    /// <exception cref="ObjectDisposedException">The index is disposed.</exception>
    public static SyntheticDriveHeader? ReadHeader(FileIndex index, char driveLetter)
    {
        ArgumentNullException.ThrowIfNull(index);
        using var borrow = index.CurrentSnapshot.Borrow();
        return borrow.Snapshot.FindDriveBlock(driveLetter) is { } driveBlock
            ? SyntheticDriveHeader.From(driveBlock.Block)
            : null;
    }
}
