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
    ///     table is a measured follow-up, so this is linear in the drive's row count. Being a
    ///     whole-drive scan rather than a single-row read, this holds a borrow on the handle's
    ///     snapshot for its duration, the same claim a <see cref="FileIndex" /> query takes, so a
    ///     release cannot unmap the rows it is walking; a disposal that arrives mid-scan waits
    ///     for it to finish.
    /// </summary>
    /// <param name="cancellationToken">
    ///     Stops the scan. Observed before the first row and then at least every 4096 rows. A
    ///     handle carries no reference to its index, so this is the caller's own token only: the
    ///     index's disposal waits this scan out rather than cancelling it.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's snapshot released.</exception>
    public IReadOnlyList<FileEntry> Children(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var borrow = Snapshot.Borrow();
        return IndexNavigation.GetChildren(borrow.Snapshot, DriveOrdinal, RowIndex, cancellationToken);
    }
}
