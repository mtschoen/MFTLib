namespace MFTLib.Index;

/// <summary>
///     Point lookups. <see cref="Find" /> walks down from the drive root matching one name per
///     level, so its cost is independent of path length and it allocates nothing until the
///     final handle. <see cref="FindByName" /> is an exact-name column scan across every
///     current drive block.
/// </summary>
internal static class LookupEngine
{
    internal static FileEntry Root(Snapshot snapshot, char driveLetter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FindDriveBlock(driveLetter) is not { } driveBlock)
        {
            throw new ArgumentException($"Drive {driveLetter} is not part of this index.", nameof(driveLetter));
        }

        return FileEntry.Create(snapshot, driveBlock.DriveOrdinal, driveBlock.Block.Header.RootRow);
    }

    internal static bool TryParseDriveLetter(string fullPath, out char driveLetter,
        out ReadOnlySpan<char> remainder)
    {
        driveLetter = '\0';
        remainder = ReadOnlySpan<char>.Empty;
        if (fullPath.Length < 3 || fullPath[1] != ':' || (fullPath[2] != '\\' && fullPath[2] != '/'))
        {
            return false;
        }

        driveLetter = fullPath[0];
        remainder = fullPath.AsSpan(3);
        return true;
    }

    internal static FileEntry? Find(Snapshot snapshot, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(fullPath);

        if (!TryParseDriveLetter(fullPath, out var driveLetter, out var remainder) ||
            snapshot.FindDriveBlock(driveLetter) is not { } driveBlock)
        {
            return null;
        }

        var currentRow = driveBlock.Block.Header.RootRow;
        foreach (var segmentRange in remainder.SplitAny(['\\', '/']))
        {
            var segment = remainder[segmentRange];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (!TryFindChild(snapshot, driveBlock.DriveOrdinal, currentRow, segment, out currentRow))
            {
                return null;
            }
        }

        return FileEntry.Create(snapshot, driveBlock.DriveOrdinal, currentRow);
    }

    internal static List<FileEntry> FindByName(Snapshot snapshot, string name, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(name);

        var results = new List<FileEntry>();
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            var scanner = new RowScanner(snapshot, driveBlock.DriveOrdinal);
            while (scanner.MoveNext())
            {
                ref readonly var row = ref scanner.Current;
                if (row.IsInUse && !row.IsDeleted &&
                    NameMatching.EqualsName(scanner.CurrentName, name, caseSensitive))
                {
                    results.Add(FileEntry.Create(snapshot, driveBlock.DriveOrdinal, scanner.CurrentRowIndex));
                }
            }
        }

        return results;
    }

    static bool TryFindChild(Snapshot snapshot, ushort driveOrdinal, uint parentRow,
        ReadOnlySpan<char> segment, out uint childRow)
    {
        var scanner = new RowScanner(snapshot, driveOrdinal);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == parentRow &&
                scanner.CurrentRowIndex != parentRow &&
                NameMatching.EqualsName(scanner.CurrentName, segment, caseSensitive: false))
            {
                childRow = scanner.CurrentRowIndex;
                return true;
            }
        }

        childRow = 0;
        return false;
    }
}
