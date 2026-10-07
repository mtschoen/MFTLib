using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Checks the records of one dump import before its block is completed. A dump is untrusted, so
///     what the index needs to navigate must hold or the import fails: the root is an allocated
///     directory at row 5 whose parent is itself, no base record number is emitted twice, and every
///     parent a record names fits the block. A record whose own number does not fit is not checked
///     here: the row writer skips and counts it, as it does for a live scan.
/// </summary>
internal sealed class MftDumpRecordValidation
{
    internal const string NoRootMessage = "The dump has no valid allocated root record.";

    internal const string DuplicateRecordMessage = "The dump contains duplicate base record numbers.";

    internal const string RecordOutsideIndexMessage =
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
        _slotCapacity = block.Header.SlotCapacity;
        _seenRows = new ulong[((ulong)_slotCapacity + 63) / 64];
    }

    /// <summary>
    ///     Passes the allocated records of each batch through, checking each one as it goes. Once the
    ///     batches end, and so before the caller completes the block, it checks the root row that
    ///     was written.
    /// </summary>
    /// <param name="batches">The parsed records; a record that is not in use is dropped.</param>
    /// <returns>The allocated records, batch by batch; an empty batch is not yielded.</returns>
    /// <exception cref="InvalidDataException">
    ///     Thrown from the enumeration: a record number repeats, a parent does not fit the block, or
    ///     the block has no valid root row once every batch is written.
    /// </exception>
    internal IEnumerable<IReadOnlyList<MftRecord>> Validate(IEnumerable<IReadOnlyList<MftRecord>> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        return ValidateBatches(batches);
    }

    IEnumerable<IReadOnlyList<MftRecord>> ValidateBatches(IEnumerable<IReadOnlyList<MftRecord>> batches)
    {
        foreach (var batch in batches)
        {
            var allocated = new List<MftRecord>(batch.Count);
            foreach (var record in batch)
            {
                if (!record.InUse)
                {
                    continue;
                }

                Check(record);
                allocated.Add(record);
            }

            if (allocated.Count > 0)
            {
                yield return allocated;
            }
        }

        ThrowUnlessRootRowIsValid();
    }

    void Check(in MftRecord record)
    {
        if (!MarkSeen(record.RecordNumber))
        {
            throw new InvalidDataException(DuplicateRecordMessage);
        }

        // A parent is required: the rows that name it are navigated through it.
        if (record.ParentRecordNumber >= _slotCapacity)
        {
            throw new InvalidDataException(RecordOutsideIndexMessage);
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

    void ThrowUnlessRootRowIsValid()
    {
        using var access = _block.TakeAccess();
        var root = _block.Rows[(int)RootRow];
        if (!root.IsInUse || root.IsDeleted || !root.IsDirectory || root.ParentRow != RootRow)
        {
            throw new InvalidDataException(NoRootMessage);
        }
    }
}
