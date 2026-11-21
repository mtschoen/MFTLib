namespace MFTLib.Index;

/// <summary>
///     The reader a block owns for the file it was scanned from. It keeps that one file open until the block is
///     released, so the bytes read for the block's entries can come from no other file, whatever later happens to
///     the path. Disposing it is the block's release; a read that leased it first keeps the file open until its
///     lease is returned.
/// </summary>
interface IBlockContentReader : IInlineContentReader, IDisposable
{
    /// <summary>
    ///     Keeps the file open for one read until the returned lease is disposed. Called under a borrow of the block,
    ///     which the block cannot be released during, so a read planned under the borrow finishes on the same file
    ///     even when a rescan replaces the block meanwhile.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The block has been released.</exception>
    IDisposable Lease();
}
