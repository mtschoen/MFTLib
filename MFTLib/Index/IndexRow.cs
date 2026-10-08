using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     A non-allocating view of one row, valid only inside the <see cref="IIndexRowVisitor.Visit" /> call
///     that receives it. Use <see cref="ToEntry" /> to keep a durable handle.
/// </summary>
public readonly ref struct IndexRow
{
    readonly Snapshot _snapshot;
    readonly ref readonly FileRow _row;
    readonly ushort _driveOrdinal;
    readonly uint _rowIndex;
#if !LEAN_INDEX_ROW
    readonly char _driveLetter;

    internal IndexRow(Snapshot snapshot, char driveLetter, ushort driveOrdinal, uint rowIndex,
        ref readonly FileRow row, ReadOnlySpan<char> name)
    {
        _snapshot = snapshot;
        _driveLetter = driveLetter;
        _driveOrdinal = driveOrdinal;
        _rowIndex = rowIndex;
        _row = ref row;
        Name = name;
    }

    /// <summary>The drive letter of the block this row lives in.</summary>
    public char DriveLetter => _driveLetter;
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal IndexRow(Snapshot snapshot, ushort driveOrdinal, uint rowIndex,
        ref readonly FileRow row, ReadOnlySpan<char> name)
    {
        _snapshot = snapshot;
        _driveOrdinal = driveOrdinal;
        _rowIndex = rowIndex;
        _row = ref row;
        Name = name;
    }

    /// <summary>The drive letter of the block this row lives in.</summary>
    public char DriveLetter => _snapshot.GetDriveBlock(_driveOrdinal).DriveLetter;
#endif

    /// <summary>The name, read straight from the mapped name pool.</summary>
    public ReadOnlySpan<char> Name { get; }

    /// <summary>The file length in bytes, or zero for directories and unknown sizes.</summary>
    public long Size => _row.Size;

    /// <summary>Whether <see cref="Size" /> is known.</summary>
    public bool IsSizeKnown => _row.SizeKnown;

    /// <summary>Whether the row is a directory.</summary>
    public bool IsDirectory => _row.IsDirectory;

    /// <summary>Whether the row is a retained deletion tombstone.</summary>
    public bool IsDeleted => _row.IsDeleted;

    /// <summary>Mints a durable handle for this row.</summary>
    public FileEntry ToEntry() => FileEntry.Create(_snapshot, _driveOrdinal, _rowIndex);
}
