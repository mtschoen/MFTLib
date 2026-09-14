namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Resolves a native filesystem path to its entry. The indexed root directory that is the
    ///     longest prefix of the path selects the block, and the remaining segments are walked
    ///     down from that block's root row, one name per level. This is the inverse of
    ///     <see cref="FileEntry.Path" />: whatever that emits, this accepts.
    /// </summary>
    public FileEntry? Find(string nativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LookupEngine.Find(CurrentSnapshot, nativePath);
    }

    /// <summary>Exact-name matches across every current drive block, folding case the way NTFS does.</summary>
    public IReadOnlyList<FileEntry> FindByName(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LookupEngine.FindByName(CurrentSnapshot, name, caseSensitive: false);
    }

    /// <summary>
    ///     The whole match set, materialized. Callers page by slicing the returned list, which is
    ///     why the count is available up front and there is no cursor.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops while applying the subtree restriction
    ///     (<see cref="SearchQuery.Under" />).
    /// </exception>
    public IReadOnlyList<FileEntry> Search(SearchQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SearchEngine.Search(CurrentSnapshot, query);
    }

    /// <summary>
    ///     Returns the largest files across the current snapshot, optionally restricted to an inclusive subtree.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     A candidate's parent chain does not resolve within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops while applying the subtree restriction
    ///     (<paramref name="under" />).
    /// </exception>
    public IReadOnlyList<FileEntry> Largest(int count, FileEntry? under = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AggregateEngine.Largest(CurrentSnapshot, count, under);
    }

    public IReadOnlyList<DuplicateGroup> DuplicateNames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AggregateEngine.DuplicateNames(CurrentSnapshot);
    }

    public FileEntry Root(char drive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LookupEngine.Root(CurrentSnapshot, drive);
    }

    /// <summary>
    ///     The ref-struct escape hatch over one drive's mapped rows, for hot paths that cannot
    ///     afford a materialized list. Internal in v1: the public surface is lists.
    /// </summary>
    internal RowScanner Scan(ushort driveOrdinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new RowScanner(CurrentSnapshot, driveOrdinal);
    }
}
