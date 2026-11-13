using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Checks the allocated records of one dump import as they are written, and the root row before the block is
///     completed. A dump is untrusted, so what the index needs to navigate must hold or the import fails: the root is
///     an allocated directory at row 5 whose parent is itself, no base record number is emitted twice, and every
///     parent a record names fits the block. A record whose own number does not fit is not checked here: the row
///     writer skips and counts it, as it does for a live scan.
/// </summary>
sealed class MftDumpRecordValidation
{
    const string NoRootMessage = "The dump has no valid allocated root record.";

    const string DuplicateRecordMessage = "The dump contains duplicate base record numbers.";

    const string RecordOutsideIndexMessage =
        "The dump contains required record numbers outside the index range.";

    const uint RootRow = 5;

    readonly BlockFile _block;
    readonly uint _slotCapacity;

    // One bit per row the block can hold. A record number past the block is rare and is tracked
    // in the set instead, so memory follows the planned block and not the largest number seen.
    readonly ulong[] _seenRows;
    HashSet<ulong>? _seenPastBlock;

    /// <summary>Prepares to validate the records written into <paramref name="block" />.</summary>
    /// <param name="block">The block the validated records are written to, not yet completed.</param>
    internal MftDumpRecordValidation(BlockFile block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _block = block;
        _slotCapacity = block.SlotCapacity;
        _seenRows = new ulong[((ulong)_slotCapacity + 63) / 64];
    }

    /// <summary>Checks one allocated record before it is written.</summary>
    /// <exception cref="InvalidDataException">The record number repeats, or its parent does not fit the block.</exception>
    internal void Check(ulong recordNumber, ulong parentRecordNumber)
    {
        if (!MarkSeen(recordNumber))
        {
            throw new InvalidDataException(DuplicateRecordMessage);
        }

        // A parent is required: the rows that name it are navigated through it.
        if (parentRecordNumber >= _slotCapacity)
        {
            throw new InvalidDataException(RecordOutsideIndexMessage);
        }
    }

    /// <summary>Checks the written root row once every record is written.</summary>
    /// <exception cref="InvalidDataException">The block has no valid root row.</exception>
    internal void ThrowUnlessRootRowIsValid()
    {
        using var access = _block.TakeAccess();
        var root = _block.Rows[(int)RootRow];
        if (!root.IsInUse || root.IsDeleted || !root.IsDirectory || root.ParentRow != RootRow)
        {
            throw new InvalidDataException(NoRootMessage);
        }
    }

    bool MarkSeen(ulong recordNumber)
    {
        if (recordNumber >= _slotCapacity)
        {
            return (_seenPastBlock ??= []).Add(recordNumber);
        }

        ref var word = ref _seenRows[recordNumber / 64];
        var bit = 1UL << (int)(recordNumber % 64);
        if ((word & bit) != 0)
        {
            return false;
        }

        word |= bit;
        return true;
    }
}
