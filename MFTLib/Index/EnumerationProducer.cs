using System.IO.Enumeration;
using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

/// <summary>
///     The managed, substrate-neutral producer. Concurrent directory readers copy retained
///     names and metadata from <see cref="FileSystemEnumerable{TResult}" /> into owned buffers.
///     One writer commits completed directories before scheduling their children. File names
///     stay in character buffers; only the directory frontier needs full path strings.
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

    internal Action? HandoffWaiting { get; init; }
    internal Action<string, int>? BeforeMoveNext { get; init; }

    /// <summary>
    ///     Samples only the root's entries and counts one more root-sized level per root
    ///     directory rather than paying for a full pre-count walk. FileIndex retries an exhausted
    ///     attempt with geometrically larger capacities, up to the block layout's capacity clamp.
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
    ///     Walks the tree concurrently, writing the root as row 0 and every entry after it into
    ///     the writer. A directory the walk options prune is written but not entered; the root is
    ///     never pruned. The walk stores its access-denied count in the header but does not call
    ///     <see cref="BlockWriter.Complete" />; the caller does. The walk stops early once the writer
    ///     reports capacity exhausted.
    /// </summary>
    /// <param name="writer">The block to write rows into.</param>
    /// <param name="progress">Receives one sample after each directory is enumerated, on the calling thread; may be null.</param>
    /// <param name="cancellationToken">Observed before the root row, each directory entry read and each row write.</param>
    /// <param name="baseLease">The base lease for this walk from <see cref="EnumerationWalkLimit" />.</param>
    /// <param name="maximumWorkers">Maximum concurrency for the walk, including the base lane.</param>
    /// <returns>The row count, the number of subtrees that could not be entered, and whether the block was too small.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer" /> or <paramref name="baseLease" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumWorkers" /> is less than 1.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the block is left partially written.</exception>
    public EnumerationResult Produce(BlockWriter writer, IProgress<IndexScanProgress>? progress,
        EnumerationWalkLimit.Lease baseLease, CancellationToken cancellationToken, int maximumWorkers = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(baseLease);
        baseLease.ThrowIfReleased();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumWorkers, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var workers = Math.Min(maximumWorkers, Environment.ProcessorCount);
        if (!writer.TryWriteRow(0, Options.RootDirectory, ReadRootColumns(Options.RootDirectory)))
        {
            return new EnumerationResult(writer.RowCount, 0, writer.CapacityExhausted);
        }

        var state = (NextRow: 1u, SkippedCount: 0);

        var reader = new EnumerationDirectoryReader(Options)
        {
            BeforeMoveNext = BeforeMoveNext
        };
        var pending = new Queue<EnumerationDirectoryWork>();
        pending.Enqueue(new EnumerationDirectoryWork(Options.RootDirectory, 0u));

        using var scheduler = new EnumerationWalkScheduler(reader, Options.Drive, workers, cancellationToken)
        {
            HandoffWaiting = HandoffWaiting
        };

        ExceptionDispatchInfo? productionFailure = null;
        try
        {
            DriveFrontier(writer, progress, scheduler, pending, ref state, cancellationToken);
        }
        catch (Exception exception)
        {
            productionFailure = ExceptionDispatchInfo.Capture(exception);
        }

        var drainFailure = scheduler.StopAndDrain();
        cancellationToken.ThrowIfCancellationRequested();
        productionFailure?.Throw();
        drainFailure?.Throw();

        writer.SetAccessDeniedSubtreeCount((uint)state.SkippedCount);
        return new EnumerationResult(writer.RowCount, state.SkippedCount, writer.CapacityExhausted);
    }

    void DriveFrontier(BlockWriter writer, IProgress<IndexScanProgress>? progress,
        EnumerationWalkScheduler scheduler, Queue<EnumerationDirectoryWork> pending,
        ref (uint NextRow, int SkippedCount) state, CancellationToken cancellationToken)
    {
        EnumerationDirectoryBuffer? inlineReady = null;
        while (inlineReady is not null || pending.Count != 0 || scheduler.OutstandingCount != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerationDirectoryBuffer completed;
            if (scheduler.TryTake(out var ready))
            {
                completed = ready;
            }
            else if (inlineReady is not null)
            {
                completed = inlineReady;
                inlineReady = null;
            }
            else if (pending.Count != 0)
            {
                var inline = pending.Dequeue();
                while (pending.Count != 0 && scheduler.TryStart(pending.Peek()))
                {
                    pending.Dequeue();
                }

                if (scheduler.TryTake(out ready))
                {
                    pending.Enqueue(inline);
                    completed = ready;
                }
                else
                {
                    inlineReady = scheduler.ReadInline(inline);
                    continue;
                }
            }
            else
            {
                completed = scheduler.Take();
            }

            CommitDirectory(completed, writer, pending, ref state.NextRow, ref state.SkippedCount, cancellationToken);
            progress?.Report(new IndexScanProgress(Options.Drive, IndexScanPhase.Enumerating, state.NextRow)
            {
                CurrentDirectory = completed.Directory.Path
            });

            if (writer.CapacityExhausted)
            {
                break;
            }
        }
    }

    internal static void CommitDirectory(EnumerationDirectoryBuffer directory, BlockWriter writer,
        Queue<EnumerationDirectoryWork> pending, ref uint nextRow, ref int skippedCount,
        CancellationToken cancellationToken)
    {
        var children = new List<EnumerationDirectoryWork>();
        foreach (var entry in directory.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowIndex = nextRow;
            if (!writer.TryWriteRow(rowIndex, directory.Name(entry), entry.Columns))
            {
                break;
            }

            nextRow = rowIndex + 1;
            if (entry.CanDescend)
            {
                children.Add(new EnumerationDirectoryWork(
                    Path.Join(directory.Directory.Path, directory.Name(entry).ToString()), rowIndex));
            }
        }

        if (directory.SubtreeSkipped)
        {
            writer.MarkSubtreeSkipped(directory.Directory.RowIndex);
            skippedCount++;
        }
        else if (!writer.CapacityExhausted)
        {
            foreach (var child in children)
            {
                pending.Enqueue(child);
            }
        }
    }

    /// <summary>
    ///     The root row's columns, observed from the root directory itself: its real attributes and last-write
    ///     time. A root that cannot be read still gets a row, with the last-write time marked unknown, because the
    ///     walk that follows reports the unreadable directory as a skipped subtree. The clock is never consulted.
    /// </summary>
    static RowColumns ReadRootColumns(string rootDirectory)
    {
        var flags = RowFlags.InUse | RowFlags.Directory;
        var attributes = FileAttributes.Directory;
        var modifiedTicks = 0L;
        try
        {
            // A missing directory reads as the 1601 epoch with no exception, which is not an observation.
            var information = new DirectoryInfo(rootDirectory);
            if (information.Exists)
            {
                attributes = information.Attributes | FileAttributes.Directory;
                modifiedTicks = information.LastWriteTimeUtc.Ticks;
            }
            else
            {
                flags |= RowFlags.LastWriteTimeUnknown;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                              or ArgumentException or NotSupportedException)
        {
            flags |= RowFlags.LastWriteTimeUnknown;
        }

        return new RowColumns(ParentRow: 0, flags, (uint)attributes, Size: 0, modifiedTicks,
            SequenceNumber: 0); // Enumeration has no NTFS sequence number.
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
}
