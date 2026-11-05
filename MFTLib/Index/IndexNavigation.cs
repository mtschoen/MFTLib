namespace MFTLib.Index;

/// <summary>
///     Walks the parent column. The upward walk mirrors the native path resolver: it stops at a
///     row whose parent is itself, caps at <see cref="BlockLayout.MaximumPathDepth" />, and
///     never revisits a row already seen on this walk, so a cyclic parent column yields a
///     truncated path instead of a hang. Depth exhaustion throws <see cref="InvalidDataException" />
///     from <see cref="BuildPath" /> and degrades to the bare name in <see cref="BuildChangePath" />.
/// </summary>
static class IndexNavigation
{
    static bool IsRootRow(BlockFile block, uint rowIndex)
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
    ///     Joins the normalized root and collected name chain using the root kind's path
    ///     rules. Host roots use host separators; dump roots use virtual separators.
    ///     Root normalization and lookup use the same policy so rendered paths round-trip.
    ///     The root key identifies the indexed root and is not a host path.
    ///     A broken parent link leaves the collected chain unrooted, including its descendants.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The parent chain does not end within <see cref="BlockLayout.MaximumPathDepth" /> hops and is not a cycle.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The root block has no configured root directory, so there is nothing to root the name
    ///     chain in. Every production block sets one; a synthetic test block need not.
    /// </exception>
    internal static string BuildPath(RootBlock rootBlock, uint rowIndex)
    {
        return TryBuildPath(rootBlock, rowIndex)
               ?? throw new InvalidDataException(
                   $"Parent chain for row {rowIndex} exceeds the supported depth of {BlockLayout.MaximumPathDepth} hops.");
    }

    /// <summary>
    ///     The path a journal change reports. A parent chain that does not end within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> hops cannot be rendered; that change reports the row's bare
    ///     name, as a detached row does, so one corrupt chain degrades one change instead of failing the batch.
    /// </summary>
    /// <exception cref="InvalidOperationException">The root block has no configured root directory.</exception>
    internal static string BuildChangePath(RootBlock rootBlock, uint rowIndex)
    {
        return TryBuildPath(rootBlock, rowIndex)
               ?? NamePool.ReadRowName(rootBlock.Block, rowIndex).ToString();
    }

    // The rendered path, or null when the parent chain does not end within the depth limit.
    static string? TryBuildPath(RootBlock rootBlock, uint rowIndex)
    {
        if (rowIndex < rootBlock.Block.Header.RowCount)
        {
            var descriptor = FileRow.ReadDescriptorWord(in rootBlock.Block.Rows[(int)rowIndex]);
            if ((FileRow.DescriptorFlags(descriptor) & RowFlags.DeletePending) != 0)
            {
                return NamePool.Read(rootBlock.Block, FileRow.DescriptorNameId(descriptor)).ToString();
            }
        }

        var segments = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        if (!TryCollectSegments(rootBlock.Block, rowIndex, ref segments, out var isDetached))
        {
            return null;
        }

        // Nothing above a broken parent link is verified, including for descendants of that link.
        if (isDetached)
        {
            return rootBlock.Kind.JoinPath(rootBlock.Block, string.Empty, segments.Values);
        }

        if (rootBlock.RootDirectoryPath is not { } rootDirectoryPath)
        {
            throw new InvalidOperationException(
                $"Root block {rootBlock.Root} has no configured root directory to build a path from.");
        }

        return rootBlock.Kind.JoinPath(rootBlock.Block, rootDirectoryPath, segments.Values);
    }

    /// <summary>
    ///     Extracts the terminal path segment from a stored name or full pre-move path. When a row is marked
    ///     <see cref="RowFlags.DeletePending" />, its name column stores the full pre-move path; its bare name is
    ///     the segment after the last path separator.
    /// </summary>
    internal static string LastPathSegmentString(NameView name)
    {
        if (name.Length == 0)
        {
            return string.Empty;
        }

        var full = name.ToString();
        var separatorIndex = full.AsSpan().LastIndexOfAny('\\', '/');
        return separatorIndex < 0 ? full : full[(separatorIndex + 1)..];
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

    // Collects the name chain upward until a root, an out-of-range parent or a repeated row ends it; false when the
    // chain is still made of new rows after the depth limit.
    static bool TryCollectSegments(BlockFile block, uint rowIndex, ref ParentChainBuffer segments,
        out bool isDetached)
    {
        var visited = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        var current = rowIndex;
        var rowCount = block.Header.RowCount;
        for (var depth = 0; depth <= BlockLayout.MaximumPathDepth; depth++)
        {
            if (current >= rowCount || visited.Contains(current) || IsRootRow(block, current))
            {
                isDetached = current >= rowCount;
                return true;
            }

            if (depth == BlockLayout.MaximumPathDepth)
            {
                break;
            }

            visited.Add(current);
            // Capture the descriptor once: names remain immutable even if the row is renamed before copying.
            segments.Add(FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in block.Rows[(int)current])));
            current = block.Rows[(int)current].ParentRow;
        }

        isDetached = false;
        return false;
    }
}
