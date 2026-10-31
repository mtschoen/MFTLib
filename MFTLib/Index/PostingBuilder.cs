namespace MFTLib.Index;

/// <summary>
///     Builds the immutable posting base by a two-pass counting sort over the descriptor of every row: count rows per
///     name, turn the counts into list starts, then place each row in its name's list. Rows are visited in ascending
///     order, so every list is ascending with no sort, and no array proportional to the block lands on the managed heap.
/// </summary>
static class PostingBuilder
{
    /// <summary>Rows between cancellation checks, matching the query row-scan cadence.</summary>
    internal const int CancellationCadence = 4096;

    /// <summary>Rows between progress reports, so a broker sees the build advance without a report per row.</summary>
    internal const int ProgressCadence = 1 << 20;

    /// <summary>
    ///     Builds the base from the rows the block holds and publishes it, restarting the delta chain empty. The caller
    ///     holds a <see cref="BlockAccessScope" /> and sets the complete flag only after this returns.
    /// </summary>
    /// <param name="block">The block whose rows are complete and which no reader is using yet.</param>
    /// <param name="progress">Called at the start of each pass and every <see cref="ProgressCadence" /> rows; may be null.</param>
    /// <param name="cancellationToken">Observed every <see cref="CancellationCadence" /> rows in both passes.</param>
    /// <exception cref="InvalidDataException">A row names an entry past the published name count.</exception>
    internal static void Build(BlockFile block, Action? progress, CancellationToken cancellationToken)
    {
        var rows = block.Rows[..(int)block.RowCount];
        var nameCount = block.NameEntryCount;
        var offsets = block.PostingOffsets;
        var postingRows = block.PostingRows;

        // The region is fresh on a new block; a test editor re-completes one, so the used prefix is cleared explicitly.
        offsets[..(int)(nameCount + 2)].Clear();
        if (block.DeltaEntryCount != 0)
        {
            block.DeltaHeads.Clear();
        }

        progress?.Invoke();
        Count(rows, offsets, nameCount, progress, cancellationToken);
        for (var index = 1; index <= nameCount + 1; index++)
        {
            offsets[index] += offsets[index - 1];
        }

        var baseRowCount = offsets[(int)nameCount + 1];
        progress?.Invoke();
        Place(rows, offsets, postingRows, progress, cancellationToken);

        // Placing advanced each start to its list's end, which is the next list's start: shift the starts back.
        for (var index = (int)nameCount; index >= 2; index--)
        {
            offsets[index] = offsets[index - 1];
        }

        offsets[1] = 0;
        block.PublishPostingBase(nameCount, baseRowCount);
    }

    static void Count(ReadOnlySpan<FileRow> rows, Span<uint> offsets, uint nameCount, Action? progress,
        CancellationToken cancellationToken)
    {
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            Checkpoint(rowIndex, progress, cancellationToken);
            var nameId = FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in rows[rowIndex]));
            if (nameId == 0)
            {
                continue;
            }

            if (nameId > nameCount)
            {
                throw new InvalidDataException($"Row {rowIndex} names entry {nameId}, past the {nameCount} published names.");
            }

            offsets[(int)nameId + 1]++;
        }
    }

    static void Place(ReadOnlySpan<FileRow> rows, Span<uint> offsets, Span<uint> postingRows, Action? progress,
        CancellationToken cancellationToken)
    {
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            Checkpoint(rowIndex, progress, cancellationToken);
            var nameId = FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in rows[rowIndex]));
            if (nameId != 0)
            {
                postingRows[(int)offsets[(int)nameId]++] = (uint)rowIndex;
            }
        }
    }

    static void Checkpoint(int rowIndex, Action? progress, CancellationToken cancellationToken)
    {
        if (rowIndex % CancellationCadence != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (rowIndex % ProgressCadence == 0 && rowIndex != 0)
        {
            progress?.Invoke();
        }
    }
}
