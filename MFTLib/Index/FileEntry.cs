using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     A 16-byte value handle onto one row of one mapped block: an object reference to the
///     snapshot that keeps the block mapped, plus a drive ordinal and a row index. Property
///     reads are pointer offsets into the mapped row. Only <see cref="Name" /> and
///     <c>Path</c> allocate. Values are live, not frozen: a journal batch that mutates the
///     underlying row changes what this handle reports, and a deleted file reads as
///     <see cref="IsDeleted" /> rather than dangling.
/// </summary>
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

    FileEntry(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        _snapshot = snapshot;
        _driveOrdinal = driveOrdinal;
        _rowIndex = rowIndex;
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
                "This FileEntry is the default value and does not reference a snapshot.");
            ObjectDisposedException.ThrowIf(snapshot.IsReleased, typeof(FileEntry));
            return snapshot;
        }
    }

    internal ushort DriveOrdinal => _driveOrdinal;

    internal uint RowIndex => _rowIndex;

    internal DriveBlock DriveBlock => Snapshot.GetDriveBlock(_driveOrdinal);

    internal ref readonly FileRow Row =>
        ref Unsafe.AsRef(in DriveBlock.Block.Rows[(int)_rowIndex]);

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

    /// <summary>Gets the row key of this entry: its drive plus its row number in that drive's block. It is not an NTFS file identifier and is not sufficient for an open by id; NTFS reuses record numbers, so it does not identify a file over time. See <see cref="IndexRecordKey" />.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public IndexRecordKey Id
    {
        get
        {
            var driveBlock = DriveBlock;
            return new IndexRecordKey(driveBlock.DriveLetter, _rowIndex, driveBlock.ProducerKind);
        }
    }

    /// <summary>Gets the current file or directory name. This allocates a managed string.</summary>
    /// <exception cref="InvalidOperationException">This is the default entry value.</exception>
    /// <exception cref="ObjectDisposedException">The owning <see cref="FileIndex" /> has been disposed.</exception>
    public string Name => new(NamePool.ReadRowName(DriveBlock.Block, _rowIndex));

    /// <summary>Gets the file length in bytes, or zero for directories and unknown sizes.</summary>
    public long Size => Row.Size;

    /// <summary>Determines whether <see cref="Size" /> is known.</summary>
    public bool SizeKnown => Row.SizeKnown;

    /// <summary>Gets the last-write time in UTC.</summary>
    public DateTime Modified => Row.ModifiedUtc;

    /// <summary>Gets the attribute flags captured in this snapshot, which can be stale after a later change.</summary>
    public FileAttributes Attributes => (FileAttributes)Row.Attributes;

    /// <summary>Determines whether the entry represents a directory.</summary>
    public bool IsDirectory => Row.IsDirectory;

    /// <summary>Determines whether the entry is a retained deletion tombstone.</summary>
    public bool IsDeleted => Row.IsDeleted;

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
            var key = Id;
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
        catch (Exception)
        {
            return null;
        }
    }
}
