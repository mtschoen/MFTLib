namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>Resolves a full path by walking down from the drive root, one name per level.</summary>
    public FileEntry? Find(string fullPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LookupEngine.Find(CurrentSnapshot, fullPath);
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
    public IReadOnlyList<FileEntry> Search(SearchQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SearchEngine.Search(CurrentSnapshot, query);
    }

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
