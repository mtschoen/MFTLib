namespace MFTLib.Index;

/// <summary>
///     A non-allocating view of one row, produced by <see cref="FileIndex.EnumerateRows" />. Valid only until
///     the enumerator advances or is disposed; call <see cref="ToEntry" /> to keep a durable handle.
/// </summary>
public readonly ref struct IndexRow
{
    readonly Snapshot _snapshot;
    readonly ref readonly FileRow _row;
    readonly ushort _driveOrdinal;
    readonly uint _rowIndex;

    internal IndexRow(Snapshot snapshot, char driveLetter, ushort driveOrdinal, uint rowIndex,
        ref readonly FileRow row, ReadOnlySpan<char> name)
    {
        _snapshot = snapshot;
        DriveLetter = driveLetter;
        _driveOrdinal = driveOrdinal;
        _rowIndex = rowIndex;
        _row = ref row;
        Name = name;
    }

    /// <summary>The name, read straight from the mapped name pool with no allocation.</summary>
    public ReadOnlySpan<char> Name { get; }

    /// <summary>The configured logical drive key, including for virtual dump drives.</summary>
    public char DriveLetter { get; }

    /// <summary>The file length in bytes, or zero for directories and unknown sizes.</summary>
    public long Size => _row.Size;

    /// <summary>Whether <see cref="Size" /> is known.</summary>
    public bool IsSizeKnown => _row.SizeKnown;

    /// <summary>Whether the row is a directory.</summary>
    public bool IsDirectory => _row.IsDirectory;

    /// <summary>Whether the row is a retained deletion tombstone.</summary>
    public bool IsDeleted => _row.IsDeleted;

    /// <summary>Returns a durable handle for this row, usable after the enumeration ends.</summary>
    public FileEntry ToEntry() => FileEntry.Create(_snapshot, _driveOrdinal, _rowIndex);
}
