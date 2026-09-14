namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>
    ///     The real filesystem path, built from the block's root directory and the name chain
    ///     using the host separator, openable and accepted by <see cref="FileIndex.Find" />.
    ///     Built once per call by walking the parent column upward. This allocates; nothing else
    ///     on the handle except <see cref="Name" /> does. The parent walk is limited to
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The parent chain does not reach the volume root within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> parent hops.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's snapshot released.</exception>
    /// <exception cref="InvalidOperationException">The drive block has no configured root directory.</exception>
    public string Path => IndexNavigation.BuildPath(Snapshot, DriveOrdinal, RowIndex);

    /// <summary>The parent directory, or null for the volume root, whose parent is itself.</summary>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's snapshot released.</exception>
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
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's snapshot released.</exception>
    public IReadOnlyList<FileEntry> Children()
    {
        return IndexNavigation.GetChildren(Snapshot, DriveOrdinal, RowIndex);
    }
}
