namespace MFTLib.Index;

/// <summary>
///     A reader's claim on a mapped root block, held for the whole of a query. While
///     one is outstanding, every deterministic release path waits rather than unmapping, so a scan
///     that is partway through a block never reads freed memory. Holding the block itself is
///     also what keeps it reachable, which is why the finalizer path needs no borrow check of its
///     own: a borrowed block cannot be collected.
/// </summary>
sealed class SnapshotBorrow : IDisposable
{
    int _acquired;

    internal SnapshotBorrow(RootBlock? rootBlock)
    {
        if (rootBlock is not null && !rootBlock.TryTakeBorrow())
        {
            throw new ObjectDisposedException(nameof(RootBlock),
                "This root block has been released and can no longer be read.");
        }

        RootBlock = rootBlock;

        // Failed construction is finalizable too; only an admitted reader owns a count.
        _acquired = 1;
    }

    /// <summary>
    ///     For a query on a directory view, the row the view was bound to when the query was admitted; null for a root that
    ///     is not a view. The query restricts itself to this row's subtree for its whole length, so a later rename of the
    ///     directory can narrow what the query saw but never widen it to the rest of the volume.
    /// </summary>
    internal uint? ViewRow { get; set; }

    /// <summary>The block this borrow keeps mapped, for the query that took it to read through, or null when the query reads no block.</summary>
    internal RootBlock? RootBlock { get; }

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
            RootBlock?.ReturnBorrow();
            GC.SuppressFinalize(this);
        }
    }

    ~SnapshotBorrow()
    {
        Dispose();
    }
}
