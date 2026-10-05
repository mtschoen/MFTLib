using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Seeds, edits and reads cache blocks through the production block writer, so a test
///     needs no knowledge of the block format. Each operation on a cache slot holds the slot's
///     owner lock for its duration and throws <see cref="InvalidOperationException" /> when an
///     open index owns the slot: close the index first.
/// </summary>
public static class SyntheticBlock
{
    /// <summary>The deepest path the index resolves. A parent chain longer than this is cut off, so a test of that limit builds a chain one row longer.</summary>
    public static int MaximumPathDepth => BlockLayout.MaximumPathDepth;

    /// <summary>The file an index looks for when it warm-starts a drive, so a seeded block is adopted only at this path.</summary>
    /// <param name="cacheDirectory">The cache directory the index is opened over.</param>
    /// <param name="driveLetter">The drive letter.</param>
    /// <param name="volumeSerial">The volume serial number the index reports for the drive.</param>
    /// <returns>The full path of the drive's block file.</returns>
    public static string CachedPath(string cacheDirectory, char driveLetter, uint volumeSerial)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        return Path.Combine(cacheDirectory, CacheDirectory.BlockFileName(driveLetter, volumeSerial));
    }

    /// <summary>
    ///     Writes a complete block into the drive's cache slot, closes it and returns its path.
    ///     Capacity is planned from the rows, with headroom for later edits.
    /// </summary>
    /// <param name="cacheDirectory">The cache directory, created when missing.</param>
    /// <param name="driveLetter">The drive letter.</param>
    /// <param name="volumeSerial">The volume serial number stamped into the header.</param>
    /// <param name="options">The header values.</param>
    /// <param name="rows">The rows to write, including the root row named by <see cref="SyntheticBlockOptions.RootRow" />.</param>
    /// <returns>The full path of the block file.</returns>
    public static string WriteCached(string cacheDirectory, char driveLetter, uint volumeSerial,
        SyntheticBlockOptions options, IEnumerable<SyntheticRow> rows)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rows);
        var path = CachedPath(cacheDirectory, driveLetter, volumeSerial);
        Directory.CreateDirectory(cacheDirectory);
        using var owner = AcquireSlot(path);
        using var block = Build(path, volumeSerial, deleteOnClose: false, options, rows);
        return path;
    }

    /// <summary>Opens an existing block, runs <paramref name="edit" />, flushes and closes it.</summary>
    /// <param name="blockPath">The block file, as returned by <see cref="WriteCached" /> or <see cref="CachedPath" />.</param>
    /// <param name="volumeSerial">The volume serial number the block was written for.</param>
    /// <param name="edit">The edit; the editor is valid only until it returns.</param>
    /// <exception cref="InvalidOperationException">The block is missing, fails validation, or is owned by an open index.</exception>
    public static void Edit(string blockPath, uint volumeSerial, Action<SyntheticBlockEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        RequireExists(blockPath);
        using var owner = AcquireSlot(blockPath);
        using var block = OpenExisting(blockPath, volumeSerial);
        var editor = new SyntheticBlockEditor(block);
        try
        {
            edit(editor);
        }
        finally
        {
            editor.Close();
        }

        block.Flush(null);
    }

    /// <summary>Reads every row in use, tombstones included, in row order.</summary>
    /// <param name="blockPath">The block file.</param>
    /// <param name="volumeSerial">The volume serial number the block was written for.</param>
    /// <returns>The rows as they are stored.</returns>
    /// <exception cref="InvalidOperationException">The block is missing, fails validation, or is owned by an open index.</exception>
    public static IReadOnlyList<SyntheticRow> ReadRows(string blockPath, uint volumeSerial)
    {
        RequireExists(blockPath);
        using var owner = AcquireSlot(blockPath);
        using var block = OpenExisting(blockPath, volumeSerial);
        var rows = new List<SyntheticRow>();
        for (var row = 0u; row < block.Header.RowCount; row++)
        {
            if (block.Rows[(int)row].IsInUse)
            {
                rows.Add(ReadRow(block, row));
            }
        }

        return rows;
    }

    /// <summary>
    ///     Creates the block file at <paramref name="path" />, writes the rows and the header
    ///     values, and returns the open handle. Internal because the handle is not public surface:
    ///     <see cref="SyntheticMftProducer" /> and the MFTLib tests transfer it to an index.
    /// </summary>
    internal static BlockFile Build(string path, uint volumeSerial, bool deleteOnClose,
        SyntheticBlockOptions options, IEnumerable<SyntheticRow> rows)
    {
        var planned = rows as IReadOnlyList<SyntheticRow> ?? rows.ToList();
        var highestRow = planned.Count == 0 ? 0u : planned.Max(row => row.Row);
        var nameBytes = planned.Sum(row => (long)row.Name.Length * sizeof(char));
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = path,
            VolumeSerial = volumeSerial,
            ProducerKind = options.ProducerKind,
            RootRow = options.RootRow,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(Math.Max(highestRow, options.RootRow) + 1),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(checked((uint)nameBytes)),
            DeleteOnClose = deleteOnClose,
            CacheTag = options.CacheTag
        });
        try
        {
            var writer = new BlockWriter(block);
            foreach (var row in planned)
            {
                WriteRow(writer, row);
            }

            writer.SetJournalCursor(options.JournalCursor.JournalId, options.JournalCursor.NextUsn);
            writer.Complete(options.CompletedUtc, null);
            return block;
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    internal static void WriteRow(BlockWriter writer, SyntheticRow row)
    {
        var flags = RowFlags.InUse;
        if (row.IsDirectory)
        {
            flags |= RowFlags.Directory;
        }

        if (row.IsTombstone)
        {
            flags |= RowFlags.Tombstone;
        }

        if (row.Size is null)
        {
            flags |= RowFlags.SizeUnknown;
        }

        var columns = new RowColumns(row.ParentRow, flags, (uint)row.Attributes, row.Size ?? 0,
            row.ModifiedUtc.Ticks, row.SequenceNumber);
        if (!writer.TryWriteRow(row.Row, row.Name, columns))
        {
            throw new InvalidOperationException(
                $"Row {row.Row} ({row.Name}) does not fit the block's capacity.");
        }
    }

    internal static SyntheticRow ReadRow(BlockFile block, uint row)
    {
        ref readonly var stored = ref block.Rows[(int)row];
        return new SyntheticRow(row, NamePool.ReadRowName(block, row).ToString(), stored.ParentRow)
        {
            IsDirectory = stored.IsDirectory,
            IsTombstone = stored.IsDeleted,
            Attributes = (FileAttributes)stored.Attributes,
            Size = stored.SizeKnown ? stored.Size : null,
            ModifiedUtc = stored.ModifiedUtc,
            SequenceNumber = block.SequenceNumbers[(int)row]
        };
    }

    static BlockOwnerLock AcquireSlot(string blockPath)
    {
        return BlockOwnerLock.TryAcquire(blockPath) ?? throw new InvalidOperationException(
            $"The block {blockPath} is owned by an open index. Dispose the index before seeding, editing or reading it.");
    }

    static void RequireExists(string blockPath)
    {
        if (!File.Exists(blockPath))
        {
            throw new InvalidOperationException($"The block {blockPath} does not exist.");
        }
    }

    static BlockFile OpenExisting(string blockPath, uint volumeSerial)
    {
        return BlockFile.Open(blockPath, volumeSerial, out var validation) ?? throw new InvalidOperationException(
            $"The block {blockPath} failed validation: {validation}.");
    }
}
