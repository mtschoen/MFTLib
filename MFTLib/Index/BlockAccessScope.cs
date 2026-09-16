namespace MFTLib.Index;

/// <summary>
///     A writer's claim on a block's mapped view, held for the whole of one
///     <see cref="BlockWriter" /> operation. While one is outstanding, <see cref="BlockFile.Dispose" />
///     waits rather than unmapping, so a write admitted before disposal began completes against
///     memory that is still mapped. Admission closes the moment disposal begins: a late writer is
///     answered with <see cref="ObjectDisposedException" /> instead of a view that is on its way
///     out. Unlike <see cref="SnapshotBorrow" /> this carries no finalizer: scopes are created and
///     disposed inside a single writer method and are never handed to a caller.
/// </summary>
internal sealed class BlockAccessScope : IDisposable
{
    readonly BlockFile _block;
    int _acquired;

    internal BlockAccessScope(BlockFile block)
    {
        _block = block;
        if (!block.TryTakeAccess())
        {
            throw new ObjectDisposedException(nameof(BlockFile),
                "This block file has been disposed and can no longer be written.");
        }

        _acquired = 1;
    }

    /// <summary>
    ///     Hands the access back, once. A second call is a caller mistake that must not decrement
    ///     the count past the accesses genuinely outstanding, so the guard is part of the contract
    ///     rather than defensive tidiness.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _acquired, 0) != 0)
        {
            _block.ReturnAccess();
        }
    }
}
