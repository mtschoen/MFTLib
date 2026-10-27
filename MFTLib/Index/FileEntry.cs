using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     A 16-byte value handle onto one file incarnation in one mapped block: an object reference to
///     the drive block, which stays mapped while the handle is held, plus a row index and the row's
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
///     anchor. <see cref="Drive" /> and <see cref="RecordKey" /> stay readable. A rename or move of
///     the same file keeps its sequence number, so the handle keeps tracking it.
///     Identity is validated per access, with no lock on the read path, and the writer publishes the new
///     sequence number before it overwrites the columns. A reader that pauses between its identity check and
///     a column read can observe the replacement's columns; re-check <see cref="IsDeleted" /> after a read that
///     must be exact, or read the <see cref="FileChange" /> event fields.
///     Handles compare equal when they name the same drive block, row and sequence number, whichever
///     query minted them.
/// </remarks>
/// <remarks>
///     The drive block reference keeps that block mapped for as long as the handle is held, so a
///     rescan that supersedes the block cannot pull the memory out from under it while the
///     owning <see cref="FileIndex" /> lives. It holds no other drive's block. Once index disposal
///     releases the block, row-backed reads and access to
///     <see cref="Drive" /> and <see cref="RecordKey" /> throw <see cref="ObjectDisposedException" />.
///     Equality and hashing use the stored handle identity and remain available, as do
///     <see cref="IsValid" /> and <see cref="IsDisposed" />. <see cref="ToString" /> remains safe
///     and returns a disposed marker. A held handle does not prevent that release.
/// </remarks>
public readonly partial record struct FileEntry
{
    readonly ushort _sequenceNumber;

    FileEntry(DriveBlock driveBlock, uint rowIndex)
    {
        NamedDriveBlock = driveBlock;
        RowIndex = rowIndex;
        _sequenceNumber = SequenceNumberOf(driveBlock, rowIndex);
    }

    /// <summary>Internal factory. Handles are only ever minted by the index and its query engines.</summary>
    internal static FileEntry Create(DriveBlock driveBlock, uint rowIndex)
    {
        ArgumentNullException.ThrowIfNull(driveBlock);
        return new FileEntry(driveBlock, rowIndex);
    }

    internal uint RowIndex { get; }

    /// <summary>
    ///     The drive block for release-checked data access. Row-backed reads, Drive and RecordKey
    ///     use this check; equality, hashing and the handle-state properties use the stored identity.
    /// </summary>
    internal DriveBlock DriveBlock
    {
        get
        {
            var driveBlock = NamedDriveBlock ?? throw new InvalidOperationException(
                "<invalid FileEntry> does not reference a drive block.");
            if (driveBlock.IsReleased)
            {
                throw new ObjectDisposedException(typeof(FileEntry).FullName, "<disposed FileEntry> has a released drive block.");
            }
            return driveBlock;
        }
    }

    /// <summary>
    ///     The row this handle was minted for, refused once a newer file has reused its slot.
    /// </summary>
    internal ref readonly FileRow Row =>
        ref Unsafe.AsRef(in CurrentBlock().Rows[(int)RowIndex]);

    /// <summary>
    ///     The sequence number the drive block holds for a row: the incarnation of the file there. Only an
    ///     MFT block stores one; an enumeration block is never mutated by the journal, so its rows keep
    ///     one incarnation for the life of the block.
    /// </summary>
    static ushort SequenceNumberOf(DriveBlock driveBlock, uint rowIndex) =>
        driveBlock.ProducerKind == ProducerKind.Mft ? driveBlock.Block.SequenceNumbers[(int)rowIndex] : (ushort)0;

    /// <summary>
    ///     True when a newer file incarnation has reused this handle's row slot, so the handle no longer
    ///     describes what the slot holds.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    bool IsSuperseded => IsSupersededIn(DriveBlock);

    bool IsSupersededIn(DriveBlock driveBlock) => _sequenceNumber != SequenceNumberOf(driveBlock, RowIndex);

    /// <summary>
    ///     The one place a column read learns its slot was reused. Every row-backed getter reads through
    ///     here, so none of them can return the replacement file's values for a superseded handle.
    /// </summary>
    BlockFile CurrentBlock()
    {
        var driveBlock = DriveBlock;
        if (IsSupersededIn(driveBlock))
        {
            throw new InvalidOperationException(
                "This FileEntry's row slot was reused after deletion by a newer file, so its columns no longer " +
                "describe the entry. Read FileChange.Path, PreviousPath and Timestamp for the facts of a change.");
        }

        return driveBlock.Block;
    }

    /// <summary>
    ///     The drive block this handle names, or null for the default value. Unlike <see cref="DriveBlock" /> it
    ///     never throws, so equality holds for default and released handles.
    /// </summary>
    internal DriveBlock? NamedDriveBlock { get; }

    /// <summary>
    ///     <see cref="IsSuperseded" /> without the released check, for a caller that already holds a borrow
    ///     that keeps the block mapped.
    /// </summary>
    internal bool IsSupersededWhileMapped => NamedDriveBlock is { } driveBlock && IsSupersededIn(driveBlock);

    /// <summary>
    ///     Compares the drive block, row index and sequence number. A superseded handle never equals the
    ///     handle of the file that reused its slot, and handles minted by different queries of one block
    ///     are equal. Available after the block is released.
    /// </summary>
    /// <param name="other">The handle to compare with.</param>
    /// <returns>true when both name the same file incarnation in the same drive block.</returns>
    public bool Equals(FileEntry other) =>
        ReferenceEquals(NamedDriveBlock, other.NamedDriveBlock) && RowIndex == other.RowIndex &&
        _sequenceNumber == other._sequenceNumber;

    /// <summary>Hashes the same drive block, row index and sequence number <see cref="Equals(FileEntry)" /> compares. Available after the block is released.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(NamedDriveBlock is { } driveBlock ? RuntimeHelpers.GetHashCode(driveBlock) : 0, RowIndex,
            _sequenceNumber);

    /// <summary>False for the default value, which references no drive block. Remains readable and true after that block is released.</summary>
    public bool IsValid => NamedDriveBlock is not null;

    /// <summary>
    ///     True once the drive block behind this handle has been released by index disposal.
    ///     Row-backed reads, <see cref="Drive" /> and <see cref="RecordKey" /> then throw <see cref="ObjectDisposedException" />; equality,
    ///     hashing, <see cref="IsValid" />, <see cref="IsDisposed" /> and <see cref="ToString" /> remain available. False for the default value,
    ///     which references no drive block: <see cref="IsValid" /> identifies that state, while <see cref="IsDeleted" /> identifies
    ///     a tombstoned or superseded row on an unreleased block.
    /// </summary>
    public bool IsDisposed => NamedDriveBlock is { IsReleased: true };

    /// <summary>Gets the configured logical drive key, including for virtual dump drives. Equals <see cref="IndexRecordKey.Drive" /> on <see cref="RecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public RootKey Drive => DriveBlock.Drive;

    /// <summary>Gets the row key of this entry: its drive plus its row number in that drive's block. It is not an NTFS file identifier and is not sufficient for an open by id; NTFS reuses record numbers, so it does not identify a file over time. See <see cref="IndexRecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public IndexRecordKey RecordKey
    {
        get
        {
            var driveBlock = DriveBlock;
            return new IndexRecordKey(driveBlock.Drive, RowIndex, driveBlock.ProducerKind);
        }
    }

    /// <summary>Gets the current file or directory name. This allocates a managed string.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public string Name => new(NamePool.ReadRowName(CurrentBlock(), RowIndex));

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
            var driveBlock = DriveBlock;
            return IsSupersededIn(driveBlock) || driveBlock.Block.Rows[(int)RowIndex].IsDeleted;
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
    ///     The path for diagnostics, or null when none can be built: a parent chain past the depth limit, a drive
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
