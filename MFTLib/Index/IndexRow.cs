namespace MFTLib.Index;

/// <summary>
///     A non-allocating view of one row, produced by <see cref="FileIndex.EnumerateRows" />. Valid only until
///     the enumerator advances or is disposed; call <see cref="ToEntry" /> to keep a durable handle.
/// </summary>
public readonly ref struct IndexRow
{
    readonly DriveBlock _driveBlock;
    readonly ref readonly FileRow _row;
    readonly uint _rowIndex;

    internal IndexRow(DriveBlock driveBlock, uint rowIndex, ref readonly FileRow row, ReadOnlySpan<char> name)
    {
        _driveBlock = driveBlock;
        _rowIndex = rowIndex;
        _row = ref row;
        Name = name;
    }

    /// <summary>The name, read straight from the mapped name pool with no allocation.</summary>
    public ReadOnlySpan<char> Name { get; }

    /// <summary>The configured logical drive key, including for virtual dump drives.</summary>
    public RootKey Drive => _driveBlock.Drive;

    /// <summary>
    ///     The file length in bytes, or zero for directories and unknown sizes. This is the length the last
    ///     scan produced; a journal content change or a journal-created row makes it unknown instead.
    /// </summary>
    public long Size => _row.Size;

    /// <summary>Whether <see cref="Size" /> is known.</summary>
    public bool IsSizeKnown => _row.SizeKnown;

    /// <summary>
    ///     The last-write time in UTC, as the last scan observed it, or <see cref="DateTime.MinValue" /> when
    ///     <see cref="IsLastWriteTimeKnown" /> is false. Same value as <see cref="FileEntry.LastWriteTime" />.
    /// </summary>
    public DateTime LastWriteTime => _row.ModifiedUtc;

    /// <summary>Whether <see cref="LastWriteTime" /> is a file system observation rather than unknown.</summary>
    public bool IsLastWriteTimeKnown => _row.LastWriteTimeKnown;

    /// <summary>The attribute flags the index holds for the row. Same value as <see cref="FileEntry.Attributes" />.</summary>
    public FileAttributes Attributes => (FileAttributes)_row.Attributes;

    /// <summary>Whether the row is a directory.</summary>
    public bool IsDirectory => _row.IsDirectory;

    /// <summary>Whether the row is a retained deletion tombstone.</summary>
    public bool IsDeleted => _row.IsDeleted;

    /// <summary>Returns a durable handle for this row, usable after the enumeration ends.</summary>
    public FileEntry ToEntry() => FileEntry.Create(_driveBlock, _rowIndex);
}
