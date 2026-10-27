using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Reads what an open <see cref="FileIndex" /> holds for a root, beyond what its status reports.</summary>
static class SyntheticIndexInspection
{
    /// <summary>
    ///     Reads the header of the root's current block under a snapshot borrow, so a concurrent
    ///     rescan or disposal waits for the read instead of unmapping the block under it.
    /// </summary>
    /// <param name="index">The open index.</param>
    /// <param name="root">The root whose block header is read.</param>
    /// <returns>The header values, or null when the root has no block (offline, declined or failed).</returns>
    /// <exception cref="ObjectDisposedException">The index is disposed.</exception>
    internal static SyntheticRootHeader? ReadHeader(FileIndex index, RootKey root)
    {
        ArgumentNullException.ThrowIfNull(index);
        using var borrow = index.BorrowCurrentSnapshot();
        return borrow.RootBlock is { } rootBlock && rootBlock.Root == root.Validated()
            ? SyntheticRootHeader.From(rootBlock.Block)
            : null;
    }
}
