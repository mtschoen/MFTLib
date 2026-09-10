using MFTLib.Index;
using MFTLib.Tests.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Stands one <see cref="FileIndex" /> up over synthetic MFT-shaped blocks and tears it down
///     again. Every source-facing member forwards to the <see cref="FakeIndexWatchSource" /> this
///     harness owns, so a test drives the seam through the harness without knowing which object
///     holds the per-drive state.
/// </summary>
internal sealed class WatchHarness : IDisposable
{
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    readonly Dictionary<char, SyntheticBlockBuilder> _blockBuilders;
    readonly Dictionary<char, IndexWatchTarget> _cursorsByDrive;
    readonly Dictionary<char, BlockFile> _producedBlocks = [];
    readonly Dictionary<char, Exception> _productionFailuresByDrive = [];
    readonly string _cacheDirectory;
    readonly char _firstDriveLetter;
    readonly FakeIndexWatchSource? _source;

    public WatchHarness(ulong journalId = 7, long nextUsn = 100)
        : this(useDefaultWatchSource: true, watchSource: null,
            [new IndexWatchTarget('T', journalId, nextUsn)])
    {
    }

    public WatchHarness(IIndexWatchSource? watchSource, ulong journalId = 7, long nextUsn = 100)
        : this(useDefaultWatchSource: false, watchSource,
            [new IndexWatchTarget('T', journalId, nextUsn)])
    {
    }

    public WatchHarness(IReadOnlyList<IndexWatchTarget> driveCursors)
        : this(useDefaultWatchSource: true, watchSource: null, driveCursors)
    {
    }

    WatchHarness(bool useDefaultWatchSource, IIndexWatchSource? watchSource,
        IReadOnlyList<IndexWatchTarget> driveCursors)
    {
        _blockBuilders = driveCursors.ToDictionary(
            cursor => char.ToUpperInvariant(cursor.DriveLetter), CreateMftBlock);
        _cursorsByDrive = driveCursors.ToDictionary(cursor => char.ToUpperInvariant(cursor.DriveLetter));
        _firstDriveLetter = char.ToUpperInvariant(driveCursors[0].DriveLetter);
        if (useDefaultWatchSource)
        {
            _source = new FakeIndexWatchSource { SourceEnding = RecordSourceEnding };
        }

        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-watch-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);

        Index = FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = _blockBuilders.Select(pair => new IndexedDrive(pair.Key,
                pair.Value.DirectoryPath, pair.Value.VolumeSerial)).ToArray(),
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = Produce,
            WatchSource = useDefaultWatchSource ? _source : watchSource
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public FileIndex Index { get; }

    public IReadOnlyDictionary<char, BlockFile> ProducedBlocks => _producedBlocks;

    public bool SourceStoppedBeforeBlockDisposed { get; private set; }

    public int SourceInvocationCount => Source.SourceInvocationCount;

    public int LiveSourceCount => Source.LiveSourceCount;

    public bool SourceCancelled => Source.SourceCancelled;

    public IReadOnlyList<char> DisarmedDrives => Source.DisarmedDrives;

    public IReadOnlyList<IndexWatchTarget> ArmedDrives => Source.ArmedDrives;

    public IReadOnlyList<string> WatchOperations => Source.WatchOperations;

    FakeIndexWatchSource Source => _source ?? throw new InvalidOperationException(
        "This harness was built with a caller-supplied watch source and owns no fake.");

    public BlockFile BlockFor(char driveLetter) => _producedBlocks[char.ToUpperInvariant(driveLetter)];

    /// <summary>The cursor the producer stamps into the block it hands back on the next scan.</summary>
    public void SetNextProducedCursor(char driveLetter, ulong journalId, long nextUsn)
    {
        var normalizedLetter = char.ToUpperInvariant(driveLetter);
        _cursorsByDrive[normalizedLetter] = new IndexWatchTarget(normalizedLetter, journalId, nextUsn);
        _blockBuilders[normalizedLetter].MutateHeader((ref header) =>
        {
            header.UsnJournalId = journalId;
            header.UsnNextUsn = nextUsn;
        });
    }

    public Task PublishAsync(WatchStreamItem item) => Source.PublishAsync(item);

    public Task Queue(WatchStreamItem item) => Source.Queue(item);

    public void HoldItemsUnread() => Source.HoldItemsUnread();

    public void ReleaseHeldItems() => Source.ReleaseHeldItems();

    public void IgnoreSourceCancellation() => Source.IgnoreCancellation();

    public void ReleaseWedgedSource() => Source.ReleaseWedgedSource();

    public void FailNextArm(Exception failure) => Source.FailNextArm(failure);

    public void FailNextDisarm(Exception failure) => Source.FailNextDisarm(failure);

    /// <summary>Makes the producer throw on this drive's next scan, so a rescan fails mid-swap.</summary>
    public void FailNextProduction(char driveLetter, Exception failure)
    {
        _productionFailuresByDrive[char.ToUpperInvariant(driveLetter)] = failure;
    }

    public Task<IReadOnlyList<IndexWatchTarget>> SourceStartedAsync() => Source.SourceStartedAsync();

    public Task SourceEndedAsync() => Source.SourceEndedAsync();

    public Task CompleteSourceAsync() => Source.CompleteSourceAsync();

    public async Task FaultSourceAsync(Exception exception)
    {
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveSourceFault(WatchFault fault)
        {
            if (fault.Kind == WatchFaultKind.Source)
            {
                announced.TrySetResult();
            }
        }

        Index.WatchFaulted += ObserveSourceFault;
        try
        {
            await Source.FaultSourceAsync(exception);
            await announced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
        }
        finally
        {
            Index.WatchFaulted -= ObserveSourceFault;
        }
    }

    public static JournalBatch Batch(char driveLetter, uint recordNumber, string fileName)
    {
        return new JournalBatch(driveLetter, [Create(recordNumber, fileName)], JournalId: 7, NextUsn: 100);
    }

    public static UsnJournalEntry Create(uint recordNumber, string fileName, ulong parentRecordNumber = 5)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            SequenceNumber = 1,
            Usn = 1,
            Timestamp = ChangeMoment,
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    public void Dispose()
    {
        // Teardown must never be the thing that wedges: a test that failed before releasing a
        // deliberately unresponsive source would otherwise hang here instead of reporting.
        _source?.ReleaseWedgedSource();
        Index.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _source?.Dispose();
        foreach (var blockBuilder in _blockBuilders.Values)
        {
            blockBuilder.Dispose();
        }

        Directory.Delete(_cacheDirectory, recursive: true);
    }

    Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driveLetter = char.ToUpperInvariant(request.DriveLetter);
        if (_productionFailuresByDrive.Remove(driveLetter, out var productionFailure))
        {
            throw productionFailure;
        }

        var cursor = _cursorsByDrive[driveLetter];
        var block = _blockBuilders[driveLetter].OpenForReading(out var validation) ??
                    throw new InvalidOperationException($"Synthetic watch block was invalid: {validation}.");
        _producedBlocks[driveLetter] = block;
        return Task.FromResult(new MftBlockProduceResult(block, cursor.JournalId, cursor.NextUsn,
            SkippedRecordCount: 0, CompactionNeeded: false));
    }

    /// <summary>
    ///     Any single block answers the question this flag asks, since one
    ///     <see cref="FileIndex.DisposeAsync" /> releases every block's mapping together.
    /// </summary>
    void RecordSourceEnding()
    {
        try
        {
            _ = BlockFor(_firstDriveLetter).Header.Generation;
            SourceStoppedBeforeBlockDisposed = true;
        }
        catch (ObjectDisposedException)
        {
            SourceStoppedBeforeBlockDisposed = false;
        }
    }

    static SyntheticBlockBuilder CreateMftBlock(IndexWatchTarget cursor)
    {
        var builder = SyntheticBlockBuilder.MftShaped();
        builder.MutateHeader((ref header) =>
        {
            header.UsnJournalId = cursor.JournalId;
            header.UsnNextUsn = cursor.NextUsn;
        });
        return builder;
    }
}
