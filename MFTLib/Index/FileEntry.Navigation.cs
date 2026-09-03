namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>
    ///     The full path, built once per call by walking the parent column upward and joining the
    ///     collected name spans. This allocates; nothing else on the handle except
    ///     <see cref="Name" /> does.
    /// </summary>
    public string Path => IndexNavigation.BuildPath(Snapshot, DriveOrdinal, RowIndex);

    /// <summary>The parent directory, or null for the volume root, whose parent is itself.</summary>
    public FileEntry? Parent
    {
        get
        {
            if (!IndexNavigation.TryGetParentRow(DriveBlock.Block, RowIndex, out var parentRow))
            {
                return null;
            }

            return Create(Snapshot, DriveOrdinal, parentRow);
        }
    }

    /// <summary>
    ///     Direct children, found by scanning the parent column. A compressed-sparse-row children
    ///     table is a measured follow-up, so this is linear in the drive's row count.
    /// </summary>
    public IReadOnlyList<FileEntry> Children()
    {
        return IndexNavigation.GetChildren(Snapshot, DriveOrdinal, RowIndex);
    }
}
