using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     A 16-byte value handle onto one file incarnation in one mapped block: an object reference to
///     the snapshot that keeps the block mapped, plus a drive ordinal, a row index and the row's
///     sequence number when the handle was minted. Property reads are pointer offsets into the mapped
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
///     snapshot minted them.
/// </remarks>
/// <remarks>
///     The snapshot reference keeps the block mapped for as long as the handle is held, so a
///     rescan that supersedes the block cannot pull the memory out from under it while the
///     owning <see cref="FileIndex" /> lives. Once index disposal releases the snapshot, every
///     property read except <see cref="IsValid" /> and <see cref="IsDisposed" /> throws
///     <see cref="ObjectDisposedException" />. A held handle does not prevent that release.
/// </remarks>
public readonly partial record struct FileEntry
{
    readonly Snapshot? _snapshot;
    readonly uint _rowIndex;
    readonly ushort _driveOrdinal;
    readonly ushort _sequenceNumber;

    FileEntry(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        _snapshot = snapshot;
        _driveOrdinal = driveOrdinal;
        _rowIndex = rowIndex;
        _sequenceNumber = SequenceNumberOf(snapshot.GetDriveBlock(driveOrdinal), rowIndex);
    }

    /// <summary>Internal factory. Handles are only ever minted by the index and its query engines.</summary>
    internal static FileEntry Create(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new FileEntry(snapshot, driveOrdinal, rowIndex);
    }

    /// <summary>
    ///     The snapshot this handle reads through. Every public member routes here, which is why
    ///     the released check lives here and nowhere else: one volatile read per access, on a path
    ///     that already does a pointer chase.
    /// </summary>
    internal Snapshot Snapshot
    {
        get
        {
            var snapshot = _snapshot ?? throw new InvalidOperationException(
                "<invalid FileEntry> does not reference a snapshot.");
            if (snapshot.IsReleased)
            {
                throw new ObjectDisposedException(typeof(FileEntry).FullName, "<disposed FileEntry> has a released snapshot.");
            }
            return snapshot;
        }
    }

    internal ushort DriveOrdinal => _driveOrdinal;

    internal uint RowIndex => _rowIndex;

    internal DriveBlock DriveBlock => Snapshot.GetDriveBlock(_driveOrdinal);

    /// <summary>
    ///     The row this handle was minted for, refused once a newer file has reused its slot.
    /// </summary>
    internal ref readonly FileRow Row =>
        ref Unsafe.AsRef(in CurrentBlock().Rows[(int)_rowIndex]);

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
    internal bool IsSuperseded => IsSupersededIn(DriveBlock);

    bool IsSupersededIn(DriveBlock driveBlock) => _sequenceNumber != SequenceNumberOf(driveBlock, _rowIndex);

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
    internal DriveBlock? NamedDriveBlock => _snapshot?.GetDriveBlock(_driveOrdinal);

    /// <summary>The snapshot that minted this handle, or null for the default value; no released check.</summary>
    internal Snapshot? OwnerSnapshot => _snapshot;

    /// <summary>
    ///     <see cref="IsSuperseded" /> without the released check, for a caller that already holds a borrow
    ///     that keeps the block mapped, or has checked that the owner snapshot is not released.
    /// </summary>
    internal bool IsSupersededWhileMapped => NamedDriveBlock is { } driveBlock && IsSupersededIn(driveBlock);

    /// <summary>
    ///     Compares the drive block, row index and sequence number. A superseded handle never equals the
    ///     handle of the file that reused its slot, and handles minted by different snapshots of one block
    ///     are equal.
    /// </summary>
    /// <param name="other">The handle to compare with.</param>
    /// <returns>true when both name the same file incarnation in the same drive block.</returns>
    public readonly bool Equals(FileEntry other) =>
        ReferenceEquals(NamedDriveBlock, other.NamedDriveBlock) && _rowIndex == other._rowIndex &&
        _sequenceNumber == other._sequenceNumber;

    /// <summary>Hashes the same drive block, row index and sequence number <see cref="Equals(FileEntry)" /> compares.</summary>
    /// <returns>The hash code.</returns>
    public override readonly int GetHashCode() =>
        HashCode.Combine(NamedDriveBlock is { } driveBlock ? RuntimeHelpers.GetHashCode(driveBlock) : 0, _rowIndex,
            _sequenceNumber);

    /// <summary>False for the default value, which references no snapshot.</summary>
    public bool IsValid => _snapshot is not null;

    /// <summary>
    ///     True once the snapshot behind this handle has been released, which happens when the
    ///     owning <see cref="FileIndex" /> is disposed. Every read on this handle throws
    ///     <see cref="ObjectDisposedException" /> from that point on. False for the default value,
    ///     which references no snapshot at all: <see cref="IsValid" /> is the question for that,
    ///     and <see cref="IsDeleted" /> is the question for a tombstoned row. Three properties,
    ///     three different questions.
    /// </summary>
    public bool IsDisposed => _snapshot is not null && _snapshot.IsReleased;

    /// <summary>Gets the configured logical drive key, including for virtual dump drives. Equals <see cref="IndexRecordKey.Drive" /> on <see cref="RecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public DriveKey Drive => DriveBlock.Drive;

    /// <summary>Gets the row key of this entry: its drive plus its row number in that drive's block. It is not an NTFS file identifier and is not sufficient for an open by id; NTFS reuses record numbers, so it does not identify a file over time. See <see cref="IndexRecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public IndexRecordKey RecordKey
    {
        get
        {
            var driveBlock = DriveBlock;
            return new IndexRecordKey(driveBlock.Drive, _rowIndex, driveBlock.ProducerKind);
        }
    }

    /// <summary>Gets the current file or directory name. This allocates a managed string.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public string Name => new(NamePool.ReadRowName(CurrentBlock(), _rowIndex));

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
            return IsSupersededIn(driveBlock) || driveBlock.Block.Rows[(int)_rowIndex].IsDeleted;
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
