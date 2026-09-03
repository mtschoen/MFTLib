namespace MFTLib.Index;

/// <summary>
///     One mapped block plus a reference count. A rescan writes a new block beside the old one
///     and drops the old block's reference; the old mapping survives until the last snapshot
///     holding it is released, which is what lets a handed-out handle stay valid across a
///     rescan without dangling.
/// </summary>
public sealed class DriveBlock
{
    readonly bool _deleteFileOnRelease;
    readonly Lock _gate = new();
    int _referenceCount;
    bool _released;
    string? _deleteAtPathOverride;

    public DriveBlock(char driveLetter, ushort driveOrdinal, BlockFile block, bool deleteFileOnRelease,
        string? rootDirectoryPath = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        DriveLetter = driveLetter;
        DriveOrdinal = driveOrdinal;
        Block = block;
        ProducerKind = block.Header.ProducerKind;
        _deleteFileOnRelease = deleteFileOnRelease;
        RootDirectoryPath = rootDirectoryPath;
    }

    public char DriveLetter { get; }

    public ushort DriveOrdinal { get; }

    public BlockFile Block { get; }

    public ProducerKind ProducerKind { get; }

    /// <summary>
    ///     The real filesystem directory an enumeration producer scanned, or null when this
    ///     block was constructed without one (every production enumeration block sets it; some
    ///     synthetic test blocks do not, since they never call <see cref="FileEntry.Open" />).
    ///     <see cref="FileEntry.Path" /> is a logical path rooted at <see cref="DriveLetter" />,
    ///     which need not be a real filesystem root, so resolving a real file requires this.
    /// </summary>
    public string? RootDirectoryPath { get; }

    public int ReferenceCount
    {
        get
        {
            lock (_gate)
            {
                return _referenceCount;
            }
        }
    }

    public bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _released;
            }
        }
    }

    /// <summary>
    ///     Takes a reference. Returns false once the block has been fully released, so a racing
    ///     snapshot creation cannot resurrect an unmapped block.
    /// </summary>
    public bool TryAddReference()
    {
        lock (_gate)
        {
            if (_released)
            {
                return false;
            }

            _referenceCount++;
            return true;
        }
    }

    /// <summary>
    ///     Overrides the path deleted on release. A rescan renames the superseded block's file
    ///     aside before the replacement takes its canonical name, so <see cref="Block" />'s own
    ///     <see cref="BlockFile.Path" /> - fixed at construction and never updated by an external
    ///     rename - no longer names the file that must be removed once this block is done. Calling
    ///     this also makes the block delete on release even when it was constructed with
    ///     <c>deleteFileOnRelease: false</c>, since a renamed-aside block is always meant to be
    ///     cleaned up eventually, cache mode or not.
    /// </summary>
    internal void ScheduleDeleteAt(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            _deleteAtPathOverride = path;
        }
    }

    /// <summary>
    ///     Cancels a previously scheduled override. Used when a rescan that renamed this block's
    ///     file aside then fails: the caller moves the file back to its original name and calls
    ///     this so a later release neither deletes the restored file nor, having already been
    ///     moved, silently no-ops on a path that no longer exists.
    /// </summary>
    internal void ClearScheduledDelete()
    {
        lock (_gate)
        {
            _deleteAtPathOverride = null;
        }
    }

    public void Release()
    {
        bool shouldUnmap;
        string? deleteOverride;
        lock (_gate)
        {
            if (_referenceCount == 0)
            {
                throw new InvalidOperationException(
                    $"Drive block {DriveLetter} was released more times than it was referenced.");
            }

            _referenceCount--;
            shouldUnmap = _referenceCount == 0;
            if (shouldUnmap)
            {
                _released = true;
            }

            deleteOverride = _deleteAtPathOverride;
        }

        if (!shouldUnmap)
        {
            return;
        }

        var path = deleteOverride ?? Block.Path;
        Block.Dispose();
        if (!_deleteFileOnRelease && deleteOverride is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The superseded block file is already unreferenced. A sharing violation here
            // leaves a stale file that the next open discards on serial or timestamp
            // mismatch, so it must not fail the rescan that triggered the release.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above: a permission error on a file
            // nothing references any more must not fail the release that triggered it.
        }
    }
}
