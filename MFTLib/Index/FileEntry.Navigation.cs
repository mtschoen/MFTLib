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
    ///     Direct children, materialized into an allocated list by scanning the parent column. A
    ///     compressed-sparse-row children table is a measured follow-up, so this is linear in
    ///     the drive's row count. Being a whole-drive scan rather than a single-row read, this holds a borrow on the handle's
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
        borrow.Snapshot.ReleaseState._readerAdmittedForTest?.Invoke();
        return IndexNavigation.GetChildren(borrow.Snapshot, DriveOrdinal, RowIndex, cancellationToken);
    }
}
