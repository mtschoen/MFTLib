namespace MFTLib.Index;

/// <summary>
///     One view's handle on a mapped block. Entries retain this handle and therefore the shared mapping;
///     the view's borrow owner refuses reads after that view closes without releasing other views.
/// </summary>
sealed class RootBlock
{
    readonly ViewBorrows? _viewBorrows;

    public RootBlock(RootKey root, BlockFile block,
        string? rootDirectoryPath = null, MftIndexSource? mftSource = null, ViewRoot? view = null)
        : this(root, new MappedBlock(block), null, rootDirectoryPath, mftSource, view)
    {
    }

    internal RootBlock(RootKey root, MappedBlock mapped, ViewBorrows? viewBorrows,
        string? rootDirectoryPath, MftIndexSource? mftSource, ViewRoot? view)
    {
        Root = root.Validated();
        Mapped = mapped;
        _viewBorrows = viewBorrows;
        Kind = RootKind.For(ProducerKind, mftSource);
        RootDirectoryPath = Kind.NormalizeRoot(rootDirectoryPath);
        View = view ?? ViewRoot.ForBlockRoot(Block.Header.RootRow);
        MatchableRootDirectoryPath = View.MatchablePath ?? Kind.ToMatchablePrefix(RootDirectoryPath);
    }

    internal RootKind Kind { get; }

    /// <summary>
    ///     The row this index is rooted at: the block's root row, or the row of the directory a view is bound to.
    ///     <see cref="RootDirectoryPath" /> stays the block's rendering root, so entry paths are always real full paths.
    /// </summary>
    internal ViewRoot View { get; }

    // I5 Borrows: acquire the view before the mapping; return in reverse. A closed view refuses
    // admission on every generation, while each mapped block independently drains its admitted readers.
    internal bool TryTakeBorrow()
    {
        if (_viewBorrows is not null && !_viewBorrows.TryTake())
        {
            return false;
        }
        if (ReleaseState.TryTakeBorrow())
        {
            return true;
        }
        _viewBorrows?.Return();
        return false;
    }

    internal void ReturnBorrow()
    {
        ReleaseState.ReturnBorrow();
        _viewBorrows?.Return();
    }

    public RootKey Root { get; }

    internal MappedBlock Mapped { get; }

    public BlockFile Block => Mapped.Block;

    /// <summary>The block's mapping and borrows, retained by the root owner after it retires this block.</summary>
    internal RootBlockRelease ReleaseState => Mapped.ReleaseState;

    /// <summary>True from the moment a release begins, before the block is unmapped.</summary>
    internal bool IsReleased => Mapped.IsReleased || _viewBorrows is { IsClosed: true };

    public ProducerKind ProducerKind => Mapped.ProducerKind;

    /// <summary>
    ///     The root used to render this block's entry paths: a host directory for live volumes
    ///     and directory walks, or an opaque virtual root for dumps. Every production block
    ///     sets one; synthetic blocks that never render paths may leave it null.
    ///     Normalized once by <see cref="RootKind.NormalizeRoot" />. Live MFT entry opening
    ///     also uses it as a path on the target volume.
    /// </summary>
    public string? RootDirectoryPath { get; }

    /// <summary>
    ///     The view's path (or, without a directory view, <see cref="RootDirectoryPath" />) in the form
    ///     <c>LookupEngine.Find</c> compares a native
    ///     path's prefix against: Windows separator spellings folded to <c>/</c> and any trailing
    ///     separator removed. On other platforms a backslash remains a filename character.
    ///     Computed once here because it is invariant for the life of the block, where recomputing
    ///     it inside the lookup allocated one or two strings per candidate block on every call.
    ///     Null when the block has no root directory, which can never match a path.
    /// </summary>
    internal string? MatchableRootDirectoryPath { get; }

    /// <summary>
    ///     Coalescing state for journal close records, owned by this block so that a
    ///     rescan replacing the block starts with no reported cycles. See
    ///     <see cref="JournalMutator" /> for how it is used.
    /// </summary>
    internal ReportedReasonCycles ReportedCycles => Mapped.ReportedCycles;
}
