namespace MFTLib.Index;

/// <summary>
///     One mapped block plus a reference count. A rescan writes a new block beside the old one
///     and drops the old block's reference; the old mapping survives until the last snapshot
///     holding it is released, which is what lets a handed-out handle stay valid across a
///     rescan without dangling.
/// </summary>
public sealed class DriveBlock
{
    readonly Lock _gate = new();
    int _referenceCount;
    bool _released;
    string? _deleteAtPathOverride;
    Action<string>? _deleteDiagnostics;

    public DriveBlock(char driveLetter, ushort driveOrdinal, BlockFile block, string? rootDirectoryPath = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        DriveLetter = driveLetter;
        DriveOrdinal = driveOrdinal;
        Block = block;
        ProducerKind = block.Header.ProducerKind;
        RootDirectoryPath = ToRootedDirectoryPath(rootDirectoryPath);
        MatchableRootDirectoryPath = ToMatchablePrefix(RootDirectoryPath);
    }

    public char DriveLetter { get; }

    public ushort DriveOrdinal { get; }

    public BlockFile Block { get; }

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

    public int ReferenceCount
    {
        get
        {
            lock (_gate)
            {
                return _referenceCount;
            }
        }
    }

    public bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _released;
            }
        }
    }

    /// <summary>
    ///     Takes a reference. Returns false once the block has been fully released, so a racing
    ///     snapshot creation cannot resurrect an unmapped block.
    /// </summary>
    public bool TryAddReference()
    {
        lock (_gate)
        {
            if (_released)
            {
                return false;
            }

            _referenceCount++;
            return true;
        }
    }

    /// <summary>
    ///     Overrides the path deleted on release. A rescan renames the superseded block's file
    ///     aside before the replacement takes its canonical name, so <see cref="Block" />'s own
    ///     <see cref="BlockFile.Path" /> - fixed at construction and never updated by an external
    ///     rename - no longer names the file that must be removed once this block is done.
    /// </summary>
    internal void ScheduleDeleteAt(string path, Action<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            _deleteAtPathOverride = path;
            _deleteDiagnostics = diagnostics;
        }
    }

    /// <summary>
    ///     Cancels a previously scheduled override. Used when a rescan that renamed this block's
    ///     file aside then fails: the caller moves the file back to its original name and calls
    ///     this so a later release neither deletes the restored file nor, having already been
    ///     moved, silently no-ops on a path that no longer exists.
    /// </summary>
    internal void ClearScheduledDelete()
    {
        lock (_gate)
        {
            _deleteAtPathOverride = null;
            _deleteDiagnostics = null;
        }
    }

    public void Release()
    {
        bool shouldUnmap;
        string? deleteOverride;
        Action<string>? deleteDiagnostics;
        lock (_gate)
        {
            if (_referenceCount == 0)
            {
                throw new InvalidOperationException(
                    $"Drive block {DriveLetter} was released more times than it was referenced.");
            }

            _referenceCount--;
            shouldUnmap = _referenceCount == 0;
            if (shouldUnmap)
            {
                _released = true;
            }

            deleteOverride = _deleteAtPathOverride;
            deleteDiagnostics = _deleteDiagnostics;
        }

        if (!shouldUnmap)
        {
            return;
        }

        var path = deleteOverride ?? Block.Path;
        Block.Dispose();
        if (deleteOverride is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
            deleteDiagnostics?.Invoke(
                $"Deleted block file '{path}': superseded by a completed rescan and fully released.");
        }
        catch (IOException)
        {
            // The superseded block file is already unreferenced. A sharing violation here
            // leaves a stale file that the next open discards on serial or timestamp
            // mismatch, so it must not fail the rescan that triggered the release.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above: a permission error on a file
            // nothing references any more must not fail the release that triggered it.
        }
    }

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
