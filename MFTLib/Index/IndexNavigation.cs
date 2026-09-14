namespace MFTLib.Index;

/// <summary>
///     Walks the parent column. The upward walk mirrors the native path resolver: it stops at a
///     row whose parent is itself, caps at <see cref="BlockLayout.MaximumPathDepth" />, and
///     never revisits a row already seen on this walk, so a cyclic parent column yields a
///     truncated path instead of a hang while depth exhaustion throws <see cref="InvalidDataException" />.
/// </summary>
internal static class IndexNavigation
{
    internal static bool IsRootRow(BlockFile block, uint rowIndex)
    {
        return block.Rows[(int)rowIndex].ParentRow == rowIndex;
    }

    /// <summary>
    ///     Reads the parent row without regard to whether either row is tombstoned. A deleted
    ///     row keeps its name and parent link so the upward walk can still resolve a full path
    ///     for a deleted file, or for a live file under a deleted directory; only <see cref="GetChildren" />
    ///     filters tombstoned rows out, because a listing should show live children only.
    /// </summary>
    internal static bool TryGetParentRow(BlockFile block, uint rowIndex, out uint parentRow)
    {
        parentRow = block.Rows[(int)rowIndex].ParentRow;
        return parentRow != rowIndex && parentRow < block.Header.RowCount;
    }

    /// <summary>
    ///     Joins the drive block's real root directory with the collected name chain, one path
    ///     component at a time, so the host's separator is the only one that appears and the
    ///     result is a path that can be opened and looked up again. The root is the block's
    ///     normalised <see cref="DriveBlock.RootDirectoryPath" />, which is what keeps the
    ///     rendered path and <c>LookupEngine.Find</c> agreeing on where the root ends. The drive
    ///     key is a display and lookup key for the index, never part of a path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The drive block has no configured root directory, so there is nothing to root the name
    ///     chain in. Every production block sets one; a synthetic test block need not.
    /// </exception>
    internal static string BuildPath(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        var driveBlock = snapshot.GetDriveBlock(driveOrdinal);
        if (driveBlock.RootDirectoryPath is not { } rootDirectoryPath)
        {
            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} has no configured root directory to build a path from.");
        }

        var segments = CollectSegments(driveBlock.Block, rowIndex);
        var components = new string[segments.Count + 1];
        components[0] = rootDirectoryPath;
        for (var index = 0; index < segments.Count; index++)
        {
            components[index + 1] = segments[segments.Count - 1 - index];
        }

        return Path.Combine(components);
    }

    /// <summary>
    ///     Direct live children only. A tombstoned row is excluded here even though
    ///     <see cref="TryGetParentRow" /> and the path walk still pass through it.
    /// </summary>
    internal static List<FileEntry> GetChildren(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        var block = snapshot.GetDriveBlock(driveOrdinal).Block;
        var rowCount = block.Header.RowCount;
        var children = new List<FileEntry>();
        var rows = block.Rows;
        for (var candidate = 0u; candidate < rowCount; candidate++)
        {
            ref readonly var row = ref rows[(int)candidate];
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == rowIndex && candidate != rowIndex)
            {
                children.Add(FileEntry.Create(snapshot, driveOrdinal, candidate));
            }
        }

        return children;
    }

    /// <summary>
    ///     True when <paramref name="candidate" /> is the ancestor itself or lives beneath it.
    ///     Entries on different drives are never under one another.
    /// </summary>
    internal static bool IsUnder(FileEntry candidate, FileEntry ancestor)
    {
        if (!candidate.IsValid || !ancestor.IsValid ||
            candidate.DriveOrdinal != ancestor.DriveOrdinal)
        {
            return false;
        }

        var block = candidate.DriveBlock.Block;
        var current = candidate.RowIndex;
        var target = ancestor.RowIndex;
        var hare = current;
        var cycleDetectionActive = true;

        for (var depth = 0; depth < BlockLayout.MaximumPathDepth; depth++)
        {
            if (current == target)
            {
                return true;
            }

            if (!TryGetParentRow(block, current, out var parent))
            {
                return false;
            }

            current = parent;
            if (current == target)
            {
                return true;
            }

            if (cycleDetectionActive)
            {
                if (TryGetParentRow(block, hare, out hare) &&
                    TryGetParentRow(block, hare, out hare))
                {
                    if (hare == current)
                    {
                        return false;
                    }
                }
                else
                {
                    cycleDetectionActive = false;
                }
            }
        }

        if (current == target)
        {
            return true;
        }

        if (!TryGetParentRow(block, current, out _))
        {
            return false;
        }

        throw new InvalidDataException(
            $"Parent chain for {candidate.Id} exceeds the supported depth of {BlockLayout.MaximumPathDepth} hops.");
    }

    static List<string> CollectSegments(BlockFile block, uint rowIndex)
    {
        var segments = new List<string>();
        var visited = new HashSet<uint>();
        var current = rowIndex;
        var rowCount = block.Header.RowCount;

        for (var depth = 0; depth < BlockLayout.MaximumPathDepth; depth++)
        {
            if (current >= rowCount || !visited.Add(current) || IsRootRow(block, current))
            {
                return segments;
            }

            var name = NamePool.ReadRowName(block, current);
            if (!name.IsEmpty)
            {
                segments.Add(new string(name));
            }

            current = block.Rows[(int)current].ParentRow;
        }

        if (current >= rowCount || visited.Contains(current) || IsRootRow(block, current))
        {
            return segments;
        }

        throw new InvalidDataException(
            $"Parent chain for row {rowIndex} exceeds the supported depth of {BlockLayout.MaximumPathDepth} hops.");
    }
}

/// <summary>
///     Narrow internal surface the test assembly uses to exercise navigation without making the
///     helpers public. MFTLib.Tests is already an InternalsVisibleTo friend of MFTLib.
/// </summary>
internal static class IndexNavigationBridge
{
    internal static bool IsUnder(FileEntry candidate, FileEntry ancestor)
    {
        return IndexNavigation.IsUnder(candidate, ancestor);
    }

    internal static uint RowIndexOf(FileEntry entry)
    {
        return entry.RowIndex;
    }
}
