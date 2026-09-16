namespace MFTLib.Index;

/// <summary>
///     A reader's claim on a snapshot's mapped blocks, held for the whole of a query. While one
///     is outstanding, every deterministic release path waits rather than unmapping, so a scan
///     that is partway through a block never reads freed memory. Holding the
///     <see cref="Snapshot" /> itself is also what keeps it reachable, which is why the finalizer
///     path needs no borrow check of its own: a borrowed snapshot cannot be collected.
/// </summary>
internal sealed class SnapshotBorrow : IDisposable
{
    readonly Snapshot _snapshot;
    int _acquired;

    internal SnapshotBorrow(Snapshot snapshot)
    {
        _snapshot = snapshot;
        if (!snapshot.ReleaseState.TryTakeBorrow())
        {
            throw new ObjectDisposedException(nameof(Snapshot),
                "This snapshot has been released and can no longer be read.");
        }

        // Failed construction is finalizable too; only an admitted reader owns a count.
        _acquired = 1;
    }

    /// <summary>The snapshot this borrow keeps mapped, for the query that took it to read through.</summary>
    internal Snapshot Snapshot => _snapshot;

    /// <summary>
    ///     Returns the borrow, once. A second call is a caller mistake that must not decrement
    ///     the count past the borrows that are genuinely outstanding, so the guard is part of the
    ///     contract rather than defensive tidiness.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _acquired, 0) != 0)
        {
            _snapshot.ReleaseState.ReturnBorrow();
            GC.SuppressFinalize(this);
        }
    }

    ~SnapshotBorrow()
    {
        Dispose();
    }
}
