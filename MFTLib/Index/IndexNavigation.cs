namespace MFTLib.Index;

/// <summary>
///     Walks the parent column. The upward walk mirrors the native path resolver: it stops at a
///     row whose parent is itself, caps at <see cref="BlockLayout.MaximumPathDepth" />, and
///     never revisits a row already seen on this walk, so a cyclic parent column yields a
///     truncated path instead of a hang while depth exhaustion throws <see cref="InvalidDataException" />.
/// </summary>
static class IndexNavigation
{
    static bool IsRootRow(BlockFile block, uint rowIndex)
    {
        return block.Rows[(int)rowIndex].ParentRow == rowIndex;
    }

    /// <summary>
    ///     True for a deleted row whose parent chain could not be verified or whose ancestor slot was reused;
    ///     see <see cref="BlockLayout.DetachedParentRow" />.
    /// </summary>
    static bool IsDetached(BlockFile block, uint rowIndex)
    {
        return block.Rows[(int)rowIndex].ParentRow == BlockLayout.DetachedParentRow;
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
    ///     Joins the normalized root and collected name chain using the root kind's path
    ///     rules. Host roots use host separators; dump roots use virtual separators.
    ///     Root normalization and lookup use the same policy so rendered paths round-trip.
    ///     The root key identifies the indexed root and is not a host path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The root block has no configured root directory, so there is nothing to root the name
    ///     chain in. Every production block sets one; a synthetic test block need not.
    /// </exception>
    internal static string BuildPath(RootBlock rootBlock, uint rowIndex)
    {
        // A detached row has no verified ancestor, so a root prefix would invent one.
        if (rowIndex < rootBlock.Block.Header.RowCount && IsDetached(rootBlock.Block, rowIndex))
        {
            return NamePool.ReadRowName(rootBlock.Block, rowIndex).ToString();
        }

        if (rootBlock.RootDirectoryPath is not { } rootDirectoryPath)
        {
            throw new InvalidOperationException(
                $"Root block {rootBlock.Root} has no configured root directory to build a path from.");
        }

        var segments = CollectSegments(rootBlock.Block, rowIndex);
        var components = new string[segments.Count + 1];
        components[0] = rootDirectoryPath;
        for (var index = 0; index < segments.Count; index++)
        {
            components[index + 1] = segments[segments.Count - 1 - index];
        }

        return rootBlock.Kind.JoinPath(components);
    }

    /// <summary>
    ///     Direct live children only. A tombstoned row is excluded here even though
    ///     <see cref="TryGetParentRow" /> and the path walk still pass through it.
    /// </summary>
    internal static List<FileEntry> GetChildren(RootBlock rootBlock, uint rowIndex,
        CancellationToken cancellationToken = default)
    {
        var children = new List<FileEntry>();
        var scanner = new RowScanner(rootBlock, cancellationToken);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            var candidate = scanner.CurrentRowIndex;
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == rowIndex && candidate != rowIndex)
            {
                children.Add(FileEntry.Create(rootBlock, candidate));
            }
        }

        return children;
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
                segments.Add(name.ToString());
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
