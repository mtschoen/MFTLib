namespace MFTLib.Index;

/// <summary>
///     A reader's claim on a snapshot of mapped drive blocks, held for the whole of a query. While
///     one is outstanding, every deterministic release path waits rather than unmapping, so a scan
///     that is partway through a block never reads freed memory. Holding the blocks themselves is
///     also what keeps them reachable, which is why the finalizer path needs no borrow check of its
///     own: a borrowed block cannot be collected.
/// </summary>
internal sealed class SnapshotBorrow : IDisposable
{
    readonly IReadOnlyList<DriveBlock> _driveBlocks;
    int _acquired;

    internal SnapshotBorrow(IReadOnlyList<DriveBlock> driveBlocks)
    {
        _driveBlocks = driveBlocks;
        for (var taken = 0; taken < driveBlocks.Count; taken++)
        {
            if (driveBlocks[taken].ReleaseState.TryTakeBorrow())
            {
                continue;
            }

            ReturnBorrows(taken);
            throw new ObjectDisposedException(nameof(DriveBlock),
                "This drive block has been released and can no longer be read.");
        }

        // Failed construction is finalizable too; only an admitted reader owns a count.
        _acquired = 1;
    }

    /// <summary>The blocks this borrow keeps mapped, for the query that took it to read through.</summary>
    internal IReadOnlyList<DriveBlock> DriveBlocks => _driveBlocks;

    /// <summary>Shared by value-type scope copies; independent of a block's release starting.</summary>
    internal bool IsReturned => _acquired == 0;

    /// <summary>
    ///     Returns the borrow, once. A second call is a caller mistake that must not decrement
    ///     the counts past the borrows that are genuinely outstanding, so the guard is part of the
    ///     contract rather than defensive tidiness.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _acquired, 0) != 0)
        {
            ReturnBorrows(_driveBlocks.Count);
            GC.SuppressFinalize(this);
        }
    }

    void ReturnBorrows(int count)
    {
        for (var index = 0; index < count; index++)
        {
            _driveBlocks[index].ReleaseState.ReturnBorrow();
        }
    }

    ~SnapshotBorrow()
    {
        Dispose();
    }
}
