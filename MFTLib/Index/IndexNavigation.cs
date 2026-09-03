using System.Text;

namespace MFTLib.Index;

/// <summary>
///     Walks the parent column. The upward walk mirrors the native path resolver: it stops at a
///     row whose parent is itself, caps at <see cref="BlockLayout.MaximumPathDepth" />, and
///     never revisits a row already seen on this walk, so a corrupt parent column yields a
///     truncated path instead of a hang.
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

    internal static string BuildPath(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        var driveBlock = snapshot.GetDriveBlock(driveOrdinal);
        var block = driveBlock.Block;
        var segments = CollectSegments(block, rowIndex);

        var builder = new StringBuilder();
        builder.Append(driveBlock.DriveLetter).Append(":\\");
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            builder.Append(segments[index]);
            if (index > 0)
            {
                builder.Append('\\');
            }
        }

        return builder.ToString();
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
        }

        return false;
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
                break;
            }

            var name = NamePool.ReadRowName(block, current);
            if (!name.IsEmpty)
            {
                segments.Add(new string(name));
            }

            current = block.Rows[(int)current].ParentRow;
        }

        return segments;
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
