namespace MFTLib.Index;

/// <summary>
///     Walks the parent column. The upward walk mirrors the native path resolver: it stops at a
///     row whose parent is itself, caps at <see cref="BlockLayout.MaximumPathDepth" />, and
///     never revisits a row already seen on this walk, so a cyclic parent column yields a
///     truncated path instead of a hang. Depth exhaustion throws <see cref="InvalidDataException" />
///     from <see cref="BuildPath" /> and degrades to the bare name in <see cref="BuildChangePath" />.
///     A parent link is followed only into a row that <see cref="IsParentRow" /> accepts; any other
///     link is broken, exactly like an out-of-range one.
/// </summary>
static class IndexNavigation
{
    const RowFlags ParentRowFlags = RowFlags.InUse | RowFlags.Directory;

    /// <summary>
    ///     Whether a row can be a parent: a producer wrote it and it is a directory. Every written row carries
    ///     <see cref="RowFlags.InUse" />, deleted and freed rows included, so a slot no producer wrote (all zero,
    ///     which would otherwise lead the walk to row 0) and a live file whose record number a child still names are
    ///     both rejected, while tombstoned, delete-pending and trusted freed directories are accepted.
    ///     Reads the flags alone, which <see cref="FileRow.ReadDescriptorWord" /> permits.
    /// </summary>
    internal static bool IsParentRow(ref readonly FileRow row)
    {
        return IsParentFlags(row.Flags);
    }

    static bool IsParentFlags(RowFlags flags)
    {
        return (flags & ParentRowFlags) == ParentRowFlags;
    }

    /// <summary>
    ///     Reads the parent row without regard to whether either row is tombstoned. A deleted
    ///     row keeps its name and parent link so the upward walk can still resolve a full path
    ///     for a deleted file, or for a live file under a deleted directory; only <see cref="GetChildren" />
    ///     filters tombstoned rows out, because a listing should show live children only.
    ///     False for a root, and for a broken link: out of range, or a row that is not <see cref="IsParentRow" />.
    /// </summary>
    internal static bool TryGetParentRow(BlockFile block, uint rowIndex, out uint parentRow)
    {
        parentRow = block.Rows[(int)rowIndex].ParentRow;
        return parentRow != rowIndex && parentRow < block.Header.RowCount &&
               IsParentRow(in block.Rows[(int)parentRow]);
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
    internal static string BuildPath(PinnedRootBlock pinned, uint rowIndex)
    {
        return TryBuildPath(pinned, rowIndex)
               ?? throw new InvalidDataException(
                   $"Parent chain for row {rowIndex} exceeds the supported depth of {BlockLayout.MaximumPathDepth} hops.");
    }

    /// <summary>
    ///     The path a journal change reports. A parent chain that does not end within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> hops cannot be rendered; that change reports the row's bare
    ///     name, as a detached row does, so one corrupt chain degrades one change instead of failing the batch.
    ///     It reads the block the journal writer wrote, so the row and its names come from one mapping.
    /// </summary>
    /// <exception cref="InvalidOperationException">The root block has no configured root directory.</exception>
    internal static string BuildChangePath(PinnedRootBlock pinned, uint rowIndex)
    {
        return TryBuildPath(pinned, rowIndex)
               ?? NamePool.ReadRowName(pinned.Block, rowIndex).ToString();
    }

    // The rendered path, or null when the parent chain does not end within the depth limit.
    static string? TryBuildPath(PinnedRootBlock pinned, uint rowIndex)
    {
        var (rootBlock, block) = pinned;
        if (rowIndex < block.Header.RowCount)
        {
            var descriptor = FileRow.ReadDescriptorWord(in block.Rows[(int)rowIndex]);
            if ((FileRow.DescriptorFlags(descriptor) & RowFlags.DeletePending) != 0)
            {
                return NamePool.Read(block, FileRow.DescriptorNameId(descriptor)).ToString();
            }
        }

        var segments = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        if (!TryCollectSegments(block, rowIndex, ref segments, out var isDetached))
        {
            return null;
        }

        // Nothing above a broken parent link is verified, including for descendants of that link.
        if (isDetached)
        {
            return rootBlock.Kind.JoinPath(block, string.Empty, segments.Values);
        }

        if (rootBlock.RootDirectoryPath is not { } rootDirectoryPath)
        {
            throw new InvalidOperationException(
                $"Root block {rootBlock.Root} has no configured root directory to build a path from.");
        }

        return rootBlock.Kind.JoinPath(block, rootDirectoryPath, segments.Values);
    }

    /// <summary>
    ///     Extracts the terminal path segment from a stored name or full pre-move path. When a row is marked
    ///     <see cref="RowFlags.DeletePending" />, its name column stores the full pre-move path; its bare name is
    ///     the segment after the last path separator.
    /// </summary>
    internal static string LastPathSegmentString(NameView name)
    {
        return name.TerminalSegment.ToString();
    }

    /// <summary>
    ///     Direct live children only. A tombstoned row is excluded here even though
    ///     <see cref="TryGetParentRow" /> and the path walk still pass through it. A row that is not
    ///     <see cref="IsParentRow" /> has none: a child naming it is detached.
    /// </summary>
    internal static List<FileEntry> GetChildren(PinnedRootBlock pinned, uint rowIndex,
        CancellationToken cancellationToken = default)
    {
        var children = new List<FileEntry>();
        if (rowIndex >= pinned.Block.Header.RowCount || !IsParentRow(in pinned.Block.Rows[(int)rowIndex]))
        {
            return children;
        }

        var scanner = new RowScanner(pinned.Block, cancellationToken);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            var candidate = scanner.CurrentRowIndex;
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == rowIndex && candidate != rowIndex)
            {
                children.Add(FileEntry.Create(pinned, candidate));
            }
        }

        return children;
    }

    // Collects the name chain upward until a root, a broken link (an out-of-range parent or a row that cannot be a
    // parent) or a repeated row ends it; false when the chain is still made of new rows after the depth limit.
    static bool TryCollectSegments(BlockFile block, uint rowIndex, ref ParentChainBuffer segments,
        out bool isDetached)
    {
        var visited = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        var current = rowIndex;
        var rowCount = block.Header.RowCount;
        for (var depth = 0; depth <= BlockLayout.MaximumPathDepth; depth++)
        {
            if (current >= rowCount)
            {
                isDetached = true;
                return true;
            }

            ref readonly var row = ref block.Rows[(int)current];
            // Capture the descriptor once: names remain immutable even if the row is renamed before copying.
            var descriptor = FileRow.ReadDescriptorWord(in row);
            var flags = FileRow.DescriptorFlags(descriptor);
            if (depth != 0 && !IsParentFlags(flags))
            {
                isDetached = true;
                return true;
            }

            var parentRow = row.ParentRow;
            if (visited.Contains(current) || parentRow == current)
            {
                isDetached = false;
                return true;
            }

            if (depth == BlockLayout.MaximumPathDepth)
            {
                break;
            }

            visited.Add(current);
            var nameId = FileRow.DescriptorNameId(descriptor);
            if ((nameId & RootKind.TerminalSegmentFlag) != 0)
            {
                // The scratch flag shares bit 31, so an on-disk identifier carrying it is rejected by the pool before tagging.
                NamePool.Read(block, nameId);
            }

            if (depth != 0 && (flags & RowFlags.DeletePending) != 0)
            {
                nameId |= RootKind.TerminalSegmentFlag;
            }

            segments.Add(nameId);
            current = parentRow;
        }

        isDetached = false;
        return false;
    }
}
