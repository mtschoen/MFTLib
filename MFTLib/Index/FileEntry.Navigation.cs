namespace MFTLib.Index;

public readonly partial record struct FileEntry
{
    /// <summary>
    ///     The path built from the block's root directory and the name chain using the host
    ///     separator. For a live entry of a real source it is a filesystem path, openable and
    ///     accepted by <see cref="FileIndex.Find" />. A dump entry renders a virtual <c>dump:/X</c>
    ///     path that cannot be opened, a detached deleted row renders its bare name, and
    ///     <see cref="FileIndex.Find" /> skips deleted entries.
    ///     Built once per call by walking the parent column upward. This allocates; other property
    ///     reads except <see cref="Name" /> do not. The parent walk is limited to
    ///     128 parent hops.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The parent chain does not reach the volume root within
    ///     128 parent hops.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's drive block released.</exception>
    /// <exception cref="InvalidOperationException">The drive block has no configured root directory, or a newer file has reused this entry's row slot.</exception>
    public string Path
    {
        get
        {
            _ = CurrentBlock();
            return IndexNavigation.BuildPath(DriveBlock, RowIndex);
        }
    }

    /// <summary>The parent directory, or null for the volume root, whose parent is itself.</summary>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's drive block released.</exception>
    /// <exception cref="InvalidOperationException">A newer file has reused this entry's row slot.</exception>
    public FileEntry? Parent
    {
        get
        {
            if (!IndexNavigation.TryGetParentRow(CurrentBlock(), RowIndex, out var parentRow))
            {
                return null;
            }

            return Create(DriveBlock, parentRow);
        }
    }

    /// <summary>
    ///     Direct children, materialized into an allocated list by scanning the parent column. A
    ///     compressed-sparse-row children table is a measured follow-up, so this is linear in
    ///     the drive's row count. Being a whole-drive scan rather than a single-row read, this holds a borrow on the handle's
    ///     drive block for its duration, the same claim a <see cref="FileIndex" /> query takes, so a
    ///     release cannot unmap the rows it is walking; a disposal that arrives mid-scan waits
    ///     for it to finish. A superseded handle, whose row slot a newer file has reused, has no children:
    ///     the list is empty rather than the replacement's children.
    /// </summary>
    /// <param name="cancellationToken">
    ///     Stops the scan. Observed before the first row and then at least every 4096 rows. A
    ///     handle carries no reference to its index, so this is the caller's own token only: the
    ///     index's disposal waits this scan out rather than cancelling it.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The owning index has been disposed and this handle's drive block released.</exception>
    public IReadOnlyList<FileEntry> Children(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driveBlock = DriveBlock;
        using var borrow = new SnapshotBorrow([driveBlock]);
        driveBlock.ReleaseState._readerAdmittedForTest?.Invoke();
        if (IsSupersededIn(driveBlock))
        {
            return [];
        }

        return IndexNavigation.GetChildren(driveBlock, RowIndex, cancellationToken);
    }
}
