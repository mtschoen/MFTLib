using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     One mapped block of one root. A <see cref="FileEntry" /> holds its root block, keeping a
///     block retired by a rescan mapped while that handle remains reachable, and nothing else: a
///     rescan of another root never waits on it. The finalizer releases a retired block once its
///     handles become unreachable while the index lives; index disposal releases every block
///     through <see cref="ReleaseState" />, which its root owner retains independently of this object.
/// </summary>
sealed class RootBlock
{
    public RootBlock(RootKey root, BlockFile block,
        string? rootDirectoryPath = null, MftIndexSource? mftSource = null, ViewRoot? view = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        Root = root.Validated();
        Block = block;
        ReleaseState = new RootBlockRelease(block);
        ProducerKind = block.Header.ProducerKind;
        Kind = RootKind.For(ProducerKind, mftSource);
        RootDirectoryPath = Kind.NormalizeRoot(rootDirectoryPath);
        View = view ?? ViewRoot.ForBlockRoot(block.Header.RootRow);
        MatchableRootDirectoryPath = View.MatchablePath ?? Kind.ToMatchablePrefix(RootDirectoryPath);
    }

    internal RootKind Kind { get; }

    /// <summary>
    ///     The row this index is rooted at: the block's root row, or the row of the directory a view is bound to.
    ///     <see cref="RootDirectoryPath" /> stays the block's rendering root, so entry paths are always real full paths.
    /// </summary>
    internal ViewRoot View { get; }

    /// <summary>
    ///     Releases the block for a caller who simply dropped every handle. The catch is
    ///     deliberately unconditional and local to this path: releasing unmaps a view and can
    ///     delete a retired block file, so a file the operating system will not unlink surfaces as
    ///     an exception, and an exception leaving a finalizer terminates the process with a stack
    ///     no consumer can act on. The worst case swallowed here is a leaked mapping or a leftover
    ///     file, which is what the caller already has. <see cref="RootBlockRelease.DrainAndReleaseAsync" />
    ///     keeps propagating, because a deterministic teardown failure is worth surfacing.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1075",
        Justification = "A finalizer is the one place where catching everything and doing nothing is the " +
                        "correct behaviour: an exception that escapes it terminates the process. There is " +
                        "nothing to log to and nothing to retry, and the swallowed worst case is a leaked " +
                        "mapping, which is strictly better than the alternative. Scoped to this method only.")]
    ~RootBlock()
    {
        try
        {
            ReleaseState.ReleaseUnborrowed();
        }
        catch (Exception)
        {
            // See this finalizer's summary.
        }
    }

    public RootKey Root { get; }

    public BlockFile Block { get; }

    /// <summary>The block's mapping and borrows, retained by the root owner after it retires this block.</summary>
    internal RootBlockRelease ReleaseState { get; }

    /// <summary>True from the moment a release begins, before the block is unmapped.</summary>
    internal bool IsReleased => ReleaseState.IsReleaseStarted;

    public ProducerKind ProducerKind { get; }

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
    internal ReportedReasonCycles ReportedCycles { get; } = new();
}
