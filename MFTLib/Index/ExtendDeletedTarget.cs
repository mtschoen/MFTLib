namespace MFTLib.Index;

/// <summary>The default delete-pending detector of <see cref="JournalMutator" />: NTFS moves a deleted-on-close file into <c>\$Extend\$Deleted</c>.</summary>
static class ExtendDeletedTarget
{
    const uint VolumeRootRecordNumber = 5;

    /// <summary>
    ///     True when the new parent row is named <c>$Deleted</c> under a row named <c>$Extend</c> whose parent is the
    ///     volume root (record 5).
    /// </summary>
    internal static bool Matches(BlockFile block, ulong newParentRecordNumber, string newName)
    {
        _ = newName;
        if (newParentRecordNumber >= block.RowCount || newParentRecordNumber == VolumeRootRecordNumber)
        {
            return false;
        }

        var deletedRowIndex = (uint)newParentRecordNumber;
        ref readonly var deletedRow = ref block.Rows[(int)deletedRowIndex];
        if (!deletedRow.IsInUse || deletedRow.IsDeleted || !RowNameEquals(block, deletedRowIndex, "$Deleted"))
        {
            return false;
        }

        var extendRowIndex = deletedRow.ParentRow;
        if (extendRowIndex == deletedRowIndex || extendRowIndex == VolumeRootRecordNumber ||
            extendRowIndex >= block.RowCount)
        {
            return false;
        }

        ref readonly var extendRow = ref block.Rows[(int)extendRowIndex];
        if (!extendRow.IsInUse || extendRow.IsDeleted || !RowNameEquals(block, extendRowIndex, "$Extend"))
        {
            return false;
        }

        var rootRowIndex = extendRow.ParentRow;
        return rootRowIndex == VolumeRootRecordNumber &&
               rootRowIndex < block.RowCount &&
               block.Rows[(int)rootRowIndex].IsInUse &&
               block.Rows[(int)rootRowIndex].ParentRow == rootRowIndex;
    }

    static bool RowNameEquals(BlockFile block, uint rowIndex, ReadOnlySpan<char> expected)
    {
        var name = NamePool.ReadRowName(block, rowIndex);
        if (name.Length != expected.Length)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[expected.Length];
        name.CopyTo(buffer);
        return MemoryExtensions.Equals(buffer, expected, StringComparison.OrdinalIgnoreCase);
    }
}
