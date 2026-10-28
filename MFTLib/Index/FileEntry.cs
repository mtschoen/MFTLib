using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     A 16-byte value handle onto one file incarnation in one mapped block: an object reference to
///     the root block, which stays mapped while the handle is held, plus a row index and the row's
///     sequence number when the handle was minted. Row-backed property reads access the mapped
///     row. Only <see cref="Name" /> and <c>Path</c> allocate. Values are live, not frozen: a journal
///     batch that mutates the underlying row changes what this handle reports, and a deleted file
///     reads as <see cref="IsDeleted" /> rather than dangling.
/// </summary>
/// <remarks>
///     NTFS reuses a deleted record's row slot for a later file and gives that file a different sequence
///     number. A handle is bound to the NTFS file reference (record number plus sequence number) it was
///     created from; distinct references never alias, while a reference NTFS itself reuses is the same
///     reference. After a
///     journal create reuses the slot the handle is superseded: it never reports the new file's
///     values and never compares equal to a handle for it. A superseded handle reads
///     <see cref="IsDeleted" />, throws <see cref="InvalidOperationException" /> from every row
///     column, returns no <see cref="Children" />, refuses <see cref="Open" /> with
///     <see cref="FileNotFoundException" /> and is rejected as a <see cref="SearchQuery.Subtree" />
///     anchor. <see cref="Root" /> and <see cref="RecordKey" /> stay readable. A rename or move of
///     the same file keeps its sequence number, so the handle keeps tracking it.
///     Identity is validated per access, with no lock on the read path, and the writer publishes the new
///     sequence number before it overwrites the columns. A reader that pauses between its identity check and
///     a column read can observe the replacement's columns; re-check <see cref="IsDeleted" /> after a read that
///     must be exact, or read the <see cref="FileChange" /> event fields.
///     Handles compare equal when they name the same root block, row and sequence number, whichever
///     query minted them.
/// </remarks>
/// <remarks>
///     The root block reference keeps that block mapped for as long as the handle is held, so a
///     rescan that supersedes the block cannot pull the memory out from under it while the
///     owning <see cref="FileIndex" /> lives. It holds no other root's block. Once index disposal
///     releases the block, row-backed reads and access to
///     <see cref="Root" /> and <see cref="RecordKey" /> throw <see cref="ObjectDisposedException" />.
///     Equality and hashing use the stored handle identity and remain available, as do
///     <see cref="IsValid" /> and <see cref="IsDisposed" />. <see cref="ToString" /> remains safe
///     and returns a disposed marker. A held handle does not prevent that release.
/// </remarks>
public readonly partial record struct FileEntry
{
    readonly ushort _sequenceNumber;

    FileEntry(RootBlock rootBlock, uint rowIndex)
    {
        NamedRootBlock = rootBlock;
        RowIndex = rowIndex;
        _sequenceNumber = SequenceNumberOf(rootBlock, rowIndex);
    }

    /// <summary>Internal factory. Handles are only ever minted by the index and its query engines.</summary>
    internal static FileEntry Create(RootBlock rootBlock, uint rowIndex)
    {
        ArgumentNullException.ThrowIfNull(rootBlock);
        return new FileEntry(rootBlock, rowIndex);
    }

    internal uint RowIndex { get; }

    /// <summary>
    ///     The root block for release-checked data access. Row-backed reads, Root and RecordKey
    ///     use this check; equality, hashing and the handle-state properties use the stored identity.
    /// </summary>
    internal RootBlock RootBlock
    {
        get
        {
            var rootBlock = NamedRootBlock ?? throw new InvalidOperationException(
                "<invalid FileEntry> does not reference a root block.");
            if (rootBlock.IsReleased)
            {
                throw new ObjectDisposedException(typeof(FileEntry).FullName, "<disposed FileEntry> has a released root block.");
            }
            return rootBlock;
        }
    }

    /// <summary>
    ///     The row this handle was minted for, refused once a newer file has reused its slot.
    /// </summary>
    internal ref readonly FileRow Row =>
        ref Unsafe.AsRef(in CurrentBlock().Rows[(int)RowIndex]);

    /// <summary>
    ///     The sequence number the root block holds for a row: the incarnation of the file there. Only an
    ///     MFT block stores one; an enumeration block is never mutated by the journal, so its rows keep
    ///     one incarnation for the life of the block.
    /// </summary>
    static ushort SequenceNumberOf(RootBlock rootBlock, uint rowIndex) =>
        rootBlock.ProducerKind == ProducerKind.Mft ? rootBlock.Block.SequenceNumbers[(int)rowIndex] : (ushort)0;

    /// <summary>
    ///     True when a newer file incarnation has reused this handle's row slot, so the handle no longer
    ///     describes what the slot holds.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    bool IsSuperseded => IsSupersededIn(RootBlock);

    bool IsSupersededIn(RootBlock rootBlock) => _sequenceNumber != SequenceNumberOf(rootBlock, RowIndex);

    /// <summary>
    ///     The one place a column read learns its slot was reused. Every row-backed getter reads through
    ///     here, so none of them can return the replacement file's values for a superseded handle.
    /// </summary>
    BlockFile CurrentBlock()
    {
        var rootBlock = RootBlock;
        if (IsSupersededIn(rootBlock))
        {
            throw new InvalidOperationException(
                "This FileEntry's row slot was reused after deletion by a newer file, so its columns no longer " +
                "describe the entry. Read FileChange.Path, PreviousPath and Timestamp for the facts of a change.");
        }

        return rootBlock.Block;
    }

    /// <summary>
    ///     The root block this handle names, or null for the default value. Unlike <see cref="RootBlock" /> it
    ///     never throws, so equality holds for default and released handles.
    /// </summary>
    internal RootBlock? NamedRootBlock { get; }

    /// <summary>
    ///     <see cref="IsSuperseded" /> without the released check, for a caller that already holds a borrow
    ///     that keeps the block mapped.
    /// </summary>
    internal bool IsSupersededWhileMapped => NamedRootBlock is { } rootBlock && IsSupersededIn(rootBlock);

    /// <summary>
    ///     Compares the root block, row index and sequence number. A superseded handle never equals the
    ///     handle of the file that reused its slot, and handles minted by different queries of one block
    ///     are equal. Available after the block is released.
    /// </summary>
    /// <param name="other">The handle to compare with.</param>
    /// <returns>true when both name the same file incarnation in the same root block.</returns>
    public bool Equals(FileEntry other) =>
        ReferenceEquals(NamedRootBlock, other.NamedRootBlock) && RowIndex == other.RowIndex &&
        _sequenceNumber == other._sequenceNumber;

    /// <summary>Hashes the same root block, row index and sequence number <see cref="Equals(FileEntry)" /> compares. Available after the block is released.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(NamedRootBlock is { } rootBlock ? RuntimeHelpers.GetHashCode(rootBlock) : 0, RowIndex,
            _sequenceNumber);

    /// <summary>False for the default value, which references no root block. Remains readable and true after that block is released.</summary>
    public bool IsValid => NamedRootBlock is not null;

    /// <summary>
    ///     True once the root block behind this handle has been released by index disposal.
    ///     Row-backed reads, <see cref="Root" /> and <see cref="RecordKey" /> then throw <see cref="ObjectDisposedException" />; equality,
    ///     hashing, <see cref="IsValid" />, <see cref="IsDisposed" /> and <see cref="ToString" /> remain available. False for the default value,
    ///     which references no root block: <see cref="IsValid" /> identifies that state, while <see cref="IsDeleted" /> identifies
    ///     a tombstoned or superseded row on an unreleased block.
    /// </summary>
    public bool IsDisposed => NamedRootBlock is { IsReleased: true };

    /// <summary>Gets the configured logical volume key, including for virtual dump roots. Equals <see cref="IndexRecordKey.Root" /> on <see cref="RecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public RootKey Root => RootBlock.Root;

    /// <summary>Gets the row key of this entry: its volume plus its row number in that volume's block. It is not an NTFS file identifier and is not sufficient for an open by id; NTFS reuses record numbers, so it does not identify a file over time. See <see cref="IndexRecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public IndexRecordKey RecordKey
    {
        get
        {
            var rootBlock = RootBlock;
            return new IndexRecordKey(rootBlock.Root, RowIndex, rootBlock.ProducerKind);
        }
    }

    /// <summary>Gets the current file or directory name. This allocates a managed string.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public string Name => NamePool.ReadRowName(CurrentBlock(), RowIndex).ToString();

    /// <summary>
    ///     Gets the file length in bytes, or zero for directories and unknown sizes. This is the length the
    ///     last scan produced. A journal content change or a journal-created row makes the size unknown instead of
    ///     leaving a stale value, so rescan or read the file for a current length.
    /// </summary>
    public long Size => Row.Size;

    /// <summary>Determines whether <see cref="Size" /> is known.</summary>
    public bool IsSizeKnown => Row.SizeKnown;

    /// <summary>
    ///     Gets the last-write time in UTC, as the last scan observed it, or <see cref="DateTime.MinValue" /> when
    ///     <see cref="IsLastWriteTimeKnown" /> is false. A journal event never writes its own time here: it marks the
    ///     value unknown instead, so a known value is always a file system observation.
    /// </summary>
    public DateTime LastWriteTime => Row.ModifiedUtc;

    /// <summary>
    ///     Determines whether <see cref="LastWriteTime" /> is a file system observation. A row the journal created, or
    ///     whose content or basic information the journal reported changed, is unknown until a rescan observes it.
    /// </summary>
    public bool IsLastWriteTimeKnown => Row.LastWriteTimeKnown;

    /// <summary>Gets the attribute flags captured in this snapshot, which can be stale after a later change.</summary>
    public FileAttributes Attributes => (FileAttributes)Row.Attributes;

    /// <summary>Determines whether the entry represents a directory.</summary>
    public bool IsDirectory => Row.IsDirectory;

    /// <summary>
    ///     Determines whether the entry is deleted: a retained deletion tombstone, or an entry whose row slot a
    ///     newer file has since reused. Never throws for a superseded handle.
    /// </summary>
    public bool IsDeleted
    {
        get
        {
            var rootBlock = RootBlock;
            return IsSupersededIn(rootBlock) || rootBlock.Block.Rows[(int)RowIndex].IsDeleted;
        }
    }

    /// <summary>Formats this entry for logs and debugger watches. It never throws, whatever state the handle is in.</summary>
    /// <returns>
    ///     The path and row key of a live entry, or its name and row key when no path can be built, or a marker
    ///     naming the default or disposed state.
    /// </returns>
    public override string ToString()
    {
        if (!IsValid)
        {
            return "<invalid FileEntry>";
        }

        try
        {
            var key = RecordKey;
            if (IsSuperseded)
            {
                return $"<superseded FileEntry> ({key})";
            }

            var name = Name;
            return $"{TryBuildPath() ?? name} ({key})";
        }
        catch (ObjectDisposedException)
        {
            return "<disposed FileEntry>";
        }
    }

    /// <summary>
    ///     The path for diagnostics, or null when none can be built: a parent chain past the depth limit, a root
    ///     with no configured root, or a release that raced the read. Formatting must not fail for any of them.
    /// </summary>
    string? TryBuildPath()
    {
        try
        {
            return Path;
        }
        catch (InvalidDataException)
        {
            // The parent chain is deeper than the path depth limit.
            return null;
        }
        catch (InvalidOperationException)
        {
            // No configured root, or a release racing the read (ObjectDisposedException derives from this).
            return null;
        }
    }
}
