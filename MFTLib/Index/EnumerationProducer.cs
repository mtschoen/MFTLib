using System.IO.Enumeration;

namespace MFTLib.Index;

/// <summary>
///     The managed, substrate-neutral producer. Walks a directory tree with
///     <see cref="FileSystemEnumerable{TResult}" /> and writes rows straight into the block as
///     it goes, so no path or name string is allocated per entry. Recursion is driven here, one
///     directory level per enumerator, because that is what lets each row know its parent and
///     lets an access denial skip exactly one subtree instead of aborting the scan.
/// </summary>
internal sealed class EnumerationProducer
{
    /// <summary>A drive is never sized below this, so a tiny sample cannot starve the block.</summary>
    const uint MinimumEstimatedRowCount = 4096;

    /// <summary>Bytes of name pool budgeted per row: 24 UTF-16 units is a generous mean name.</summary>
    const uint EstimatedNameBytesPerRow = 48;

    /// <summary>
    ///     Bytes the OS fills per directory fetch, sized up so a network share pays one round
    ///     trip per buffer. Never worse than the framework default on local NTFS or a network
    ///     share and 44% faster on local NTFS; 1 MB regresses badly on directory-heavy trees.
    ///     Measurement: https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/29
    ///     Docs: https://learn.microsoft.com/en-us/dotnet/api/system.io.enumerationoptions.buffersize
    /// </summary>
    internal const int EnumerationBufferSizeBytes = 64 * 1024;

    /// <summary>Creates a producer that walks the tree named by the options.</summary>
    /// <param name="options">The root directory to walk and the drive letter to report progress under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    public EnumerationProducer(EnumerationProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The root directory and progress drive letter fixed at construction; they never change during a walk.</summary>
    EnumerationProducerOptions Options { get; }

    /// <summary>
    ///     Samples the first two levels and extrapolates rather than paying for a full
    ///     pre-count walk. Under-estimating is absorbed by the block's headroom, and true
    ///     exhaustion sets the compaction-needed flag.
    /// </summary>
    public static uint EstimateRowCount(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        try
        {
            var topLevel = 0u;
            var directories = 0u;
            foreach (var path in Directory.EnumerateFileSystemEntries(rootDirectory))
            {
                topLevel++;
                if (Directory.Exists(path))
                {
                    directories++;
                }
            }

            var estimate = topLevel + (directories * topLevel);
            return Math.Max(estimate, MinimumEstimatedRowCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MinimumEstimatedRowCount;
        }
    }

    /// <summary>Budgets name-pool bytes for a row estimate at a fixed mean of 24 UTF-16 units per name.</summary>
    /// <param name="estimatedRowCount">Row estimate, typically from <see cref="EstimateRowCount" />.</param>
    /// <returns>The estimated name-pool size in bytes.</returns>
    /// <exception cref="OverflowException">The product exceeds <see cref="UInt32.MaxValue" />.</exception>
    public static uint EstimateNamePoolBytes(uint estimatedRowCount)
    {
        return checked(estimatedRowCount * EstimatedNameBytesPerRow);
    }

    /// <summary>
    ///     Walks the tree breadth first, writing the root as row 0 and every entry after it into
    ///     the writer. It does not call <see cref="BlockWriter.Complete" />; the caller does. The
    ///     walk stops early once the writer reports compaction needed.
    /// </summary>
    /// <param name="writer">The block to write rows into.</param>
    /// <param name="progress">Receives one sample after each directory is enumerated, on the calling thread; may be null.</param>
    /// <param name="cancellationToken">Observed before the root row and before each directory.</param>
    /// <returns>The row count, the number of subtrees that could not be entered, and whether the block was too small.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer" /> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the block is left partially written.</exception>
    public EnumerationResult Produce(BlockWriter writer, IProgress<IndexScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new WalkState(writer);
        writer.TryWriteRow(0, Options.RootDirectory,
            new RowColumns(ParentRow: 0, RowFlags.InUse | RowFlags.Directory,
                (uint)FileAttributes.Directory, Size: 0, DateTime.UtcNow.Ticks,
                SequenceNumber: 0)); // Enumeration has no NTFS sequence number.
        state.NextRow = 1;

        var pending = new Queue<(string Path, uint RowIndex)>();
        pending.Enqueue((Options.RootDirectory, 0u));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directoryPath, directoryRow) = pending.Dequeue();
            EnumerateOneDirectory(state, directoryPath, directoryRow, pending, cancellationToken);
            progress?.Report(new IndexScanProgress(Options.DriveLetter, IndexScanPhase.Enumerating, state.NextRow)
            {
                CurrentDirectory = directoryPath
            });

            // Every write below capacity has already failed once compaction is needed, so
            // opening further queued directories would only pay I/O for more of the same
            // no-op writes. Stop the walk here instead.
            if (writer.CompactionNeeded)
            {
                break;
            }
        }

        return new EnumerationResult(writer.RowCount, state.AccessDeniedSubtreeCount, writer.CompactionNeeded);
    }

    internal static EnumerationOptions CreateDirectoryEnumerationOptions()
    {
        return new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            // An MFT scan sees every record regardless of the hidden or system
            // attribute, so the managed walk must too: the .NET default of skipping
            // Hidden | System would make an enumeration block disagree with an
            // MFT-derived one over the same volume.
            AttributesToSkip = 0,
            BufferSize = EnumerationBufferSizeBytes
        };
    }

    static void EnumerateOneDirectory(WalkState state, string directoryPath, uint directoryRow,
        Queue<(string Path, uint RowIndex)> pending, CancellationToken cancellationToken)
    {
        var childDirectories = new List<(string Path, uint RowIndex)>();

        try
        {
            // Construction itself opens the directory handle and can throw, so it has to sit
            // inside the same try as the walk: a missing or denied root directory fails here,
            // not on the first MoveNext.
            var enumerable = new FileSystemEnumerable<bool>(directoryPath,
                (ref entry) => state.WriteRow(ref entry, directoryRow, childDirectories),
                CreateDirectoryEnumerationOptions());

            foreach (var _ in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or DirectoryNotFoundException or IOException)
        {
            // Spec section 7: mark the row, skip the subtree, count it in the drive's warning,
            // and keep traversing. A denied or vanished directory is not a failed scan.
            state.Writer.MarkSubtreeSkipped(directoryRow);
            state.AccessDeniedSubtreeCount++;
            return;
        }

        foreach (var child in childDirectories)
        {
            pending.Enqueue(child);
        }
    }

    /// <summary>
    ///     Mutable walk state. It exists so the transform delegate can write rows without
    ///     capturing a ref local, which a lambda cannot do.
    /// </summary>
    sealed class WalkState(BlockWriter writer)
    {
        public BlockWriter Writer { get; } = writer;

        public uint NextRow { get; set; }

        public int AccessDeniedSubtreeCount { get; set; }

        /// <summary>
        ///     Writes one entry's row from the enumerator's own buffers. Returns whether the entry
        ///     was a directory; the enumerable's element type is unused, the write is the point.
        ///     A reparse point (a directory symbolic link or an NTFS junction) is recorded like
        ///     any other row but never queued for traversal: an MFT record exists for it too, but
        ///     path resolution does not cross it, and a directory reparse point can point back up
        ///     its own tree, so following it here would walk forever.
        /// </summary>
        public bool WriteRow(ref FileSystemEntry entry, uint parentRow,
            List<(string Path, uint RowIndex)> childDirectories)
        {
            var isDirectory = entry.IsDirectory;
            var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            var rowIndex = NextRow;
            var flags = RowFlags.InUse | (isDirectory ? RowFlags.Directory : RowFlags.None);
            var size = isDirectory ? 0 : entry.Length;

            var columns = new RowColumns(parentRow, flags, (uint)entry.Attributes, size,
                entry.LastWriteTimeUtc.UtcDateTime.Ticks,
                SequenceNumber: 0); // Enumeration has no NTFS sequence number.
            if (!Writer.TryWriteRow(rowIndex, entry.FileName, columns))
            {
                return isDirectory;
            }

            NextRow = rowIndex + 1;
            if (isDirectory && !isReparsePoint)
            {
                childDirectories.Add((entry.ToFullPath(), rowIndex));
            }

            return isDirectory;
        }
    }
}
