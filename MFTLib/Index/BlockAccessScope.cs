namespace MFTLib.Index;

/// <summary>
///     A writer's claim on a block's mapped view, held for the whole of one
///     <see cref="BlockWriter" /> operation. While one is outstanding, <see cref="BlockFile.Dispose" />
///     waits rather than unmapping, so a write admitted before disposal began completes against
///     memory that is still mapped. Admission closes the moment disposal begins: a late writer is
///     answered with <see cref="ObjectDisposedException" /> instead of a view that is on its way
///     out. A ref struct, so a scope cannot be boxed, captured, stored in a field or carried across an
///     <c>await</c>: it lives and dies in the one synchronous method that took it, and is disposed
///     exactly once by the <c>using</c> that declared it.
/// </summary>
internal readonly ref struct BlockAccessScope : IDisposable
{
    readonly BlockFile _block;

    internal BlockAccessScope(BlockFile block)
    {
        if (!block.TryTakeAccess())
        {
            throw new ObjectDisposedException(nameof(BlockFile),
                "This block file has been disposed and can no longer be written.");
        }

        _block = block;
    }

    public void Dispose()
    {
        _block.ReturnAccess();
    }
}
