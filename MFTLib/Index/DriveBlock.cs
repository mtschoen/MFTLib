using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     One mapped block of one drive. A <see cref="FileEntry" /> holds its drive block, keeping a
///     block retired by a rescan mapped while that handle remains reachable, and nothing else: a
///     rescan of another drive never waits on it. The finalizer releases a retired block once its
///     handles become unreachable while the index lives; index disposal releases every block
///     through <see cref="ReleaseState" />, which it retains independently of this object.
/// </summary>
internal sealed class DriveBlock
{
    public DriveBlock(DriveKey driveLetter, ushort driveOrdinal, BlockFile block, string? rootDirectoryPath = null,
        bool isMftDump = false)
    {
        ArgumentNullException.ThrowIfNull(block);
        Drive = driveLetter.Validated();
        DriveOrdinal = driveOrdinal;
        Block = block;
        ReleaseState = new DriveBlockRelease(block);
        ProducerKind = block.Header.ProducerKind;
        IsMftDump = isMftDump;
        RootDirectoryPath = isMftDump ? rootDirectoryPath : ToRootedDirectoryPath(rootDirectoryPath);
        MatchableRootDirectoryPath = isMftDump
            ? MftDumpPaths.ToMatchablePrefix(RootDirectoryPath)
            : ToMatchablePrefix(RootDirectoryPath);
    }

    /// <summary>
    ///     Releases the block for a caller who simply dropped every handle. The catch is
    ///     deliberately unconditional and local to this path: releasing unmaps a view and can
    ///     delete a retired block file, so a file the operating system will not unlink surfaces as
    ///     an exception, and an exception leaving a finalizer terminates the process with a stack
    ///     no consumer can act on. The worst case swallowed here is a leaked mapping or a leftover
    ///     file, which is what the caller already has. <see cref="DriveBlockRelease.ReleaseAsync" />
    ///     keeps propagating, because a deterministic teardown failure is worth surfacing.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1075",
        Justification = "A finalizer is the one place where catching everything and doing nothing is the " +
                        "correct behaviour: an exception that escapes it terminates the process. There is " +
                        "nothing to log to and nothing to retry, and the swallowed worst case is a leaked " +
                        "mapping, which is strictly better than the alternative. Scoped to this method only.")]
    ~DriveBlock()
    {
        try
        {
            ReleaseState.Release();
        }
        catch (Exception)
        {
            // See this finalizer's summary.
        }
    }

    /// <summary>
    ///     True when this block describes an MFT dump file. Its root is the opaque virtual root
    ///     <c>dump:/{driveLetter}</c>, never a host path, so nothing may probe, open or journal it
    ///     as the live volume that shares its letter.
    /// </summary>
    public bool IsMftDump { get; }

    public DriveKey Drive { get; }

    public ushort DriveOrdinal { get; }

    public BlockFile Block { get; }

    /// <summary>The block's mapping and borrows, retained by the index after it retires this block.</summary>
    internal DriveBlockRelease ReleaseState { get; }

    /// <summary>True from the moment a release begins, before the block is unmapped.</summary>
    internal bool IsReleased => ReleaseState.IsReleaseStarted;

    public ProducerKind ProducerKind { get; }

    /// <summary>
    ///     The base of every real filesystem path this block renders through <see cref="FileEntry.Path" />.
    ///     Required for path rendering and <see cref="FileEntry.Open" /> on both producer kinds.
    ///     Every production block sets it; synthetic blocks that never render or open paths may
    ///     leave it null. MFT entries also use it as a path on the target volume to open by file id.
    ///     Normalised at construction by <see cref="ToRootedDirectoryPath" />, so what is stored is
    ///     always rooted even when the caller supplied a bare drive specifier.
    /// </summary>
    public string? RootDirectoryPath { get; }

    /// <summary>
    ///     <see cref="RootDirectoryPath" /> in the form <c>LookupEngine.Find</c> compares a native
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

    /// <summary>
    ///     The single place a caller-supplied root is normalised, so path rendering and path
    ///     lookup can never disagree about what this block's root is. A bare drive specifier such
    ///     as <c>C:</c> is drive-relative on Windows: joined with a name chain it yields
    ///     <c>C:Windows</c>, which resolves against that drive's per-process current directory
    ///     rather than its root, and which no lookup would then accept because the character after
    ///     the root is a name character instead of a separator. A separator is appended so the
    ///     result is rooted. Resolving through <c>Path.GetFullPath</c> would be wrong here, since
    ///     that resolves against the current process directory rather than the drive's root.
    /// </summary>
    static string? ToRootedDirectoryPath(string? rootDirectoryPath)
    {
        if (rootDirectoryPath is not { Length: 2 } specifier ||
            specifier[1] != ':' || !char.IsAsciiLetter(specifier[0]))
        {
            return rootDirectoryPath;
        }

        return specifier + Path.DirectorySeparatorChar;
    }

    /// <summary>
    ///     Windows separator spellings are folded to one character and any trailing separator is
    ///     removed, which is the form a path prefix is compared against. On other platforms a
    ///     backslash remains a filename character. Null for a block with no root directory, and
    ///     for an empty one, because neither can ever match a path.
    /// </summary>
    static string? ToMatchablePrefix(string? rootDirectoryPath)
    {
        if (rootDirectoryPath is not { Length: > 0 } root)
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? root.Replace('\\', '/').TrimEnd('/')
            : root.TrimEnd(Path.DirectorySeparatorChar);
    }
}
