using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Stands one <see cref="FileIndex" /> up over synthetic MFT-shaped blocks, one per drive
///     letter (<c>T</c>, <c>U</c> and <c>V</c> unless told otherwise), with a scripted producer
///     and a <see cref="FakeIndexWatchSource" />, and tears it down again. It collects every
///     <see cref="FileIndex.Changed" /> change and every <see cref="FileIndex.WatchFaulted" /> fault.
/// </summary>
internal sealed class WatchHarness : IDisposable
{
    public const ulong JournalId = 7;
    public const long NextUsn = 100;

    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    readonly Dictionary<char, SyntheticBlockBuilder> _blockBuilders;
    readonly Dictionary<char, IndexWatchTarget> _cursorsByDrive;
    readonly ConcurrentDictionary<char, BlockFile> _producedBlocks = [];
    readonly ConcurrentDictionary<char, Exception> _productionFailuresByDrive = [];
    readonly ConcurrentDictionary<char, TestGate> _productionHoldsByDrive = [];
    readonly List<TestGate> _gates = [];
    readonly ConcurrentQueue<FileChange> _changes = [];
    readonly List<WatchFault> _faults = [];
    readonly List<(Func<WatchFault, bool> Match, TaskCompletionSource<WatchFault> Completion)> _faultWaiters = [];
    readonly string _cacheDirectory;

    public WatchHarness(params char[] driveLetters)
    {
        if (driveLetters.Length == 0)
        {
            driveLetters = ['T', 'U', 'V'];
        }

        _cursorsByDrive = driveLetters.Select(char.ToUpperInvariant)
            .ToDictionary(letter => letter, letter => new IndexWatchTarget(letter, JournalId, NextUsn));
        _blockBuilders = _cursorsByDrive.ToDictionary(pair => pair.Key, pair => CreateMftBlock(pair.Value));
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-watch-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);

        Index = FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = _blockBuilders.Select(pair => new IndexedDrive(pair.Key,
                pair.Value.DirectoryPath, pair.Value.VolumeSerial)).ToArray(),
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = Produce,
            WatchSource = Source
        }, CancellationToken.None).GetAwaiter().GetResult();
        Index.Changed += _changes.Enqueue;
        Index.WatchFaulted += RecordFault;
    }

    public FileIndex Index { get; }

    public FakeIndexWatchSource Source { get; } = new();

    public IReadOnlyCollection<FileChange> Changes => _changes;

    public IReadOnlyList<WatchFault> Faults
    {
        get
        {
            lock (_faults)
            {
                return [.. _faults];
            }
        }
    }

    public BlockFile BlockFor(char driveLetter) => _producedBlocks[char.ToUpperInvariant(driveLetter)];

    public DriveStatus DriveFor(char driveLetter) =>
        Index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(driveLetter));

    /// <summary>Completes with the first fault of <paramref name="kind" /> on the drive, recorded or future.</summary>
    public Task<WatchFault> WaitForFaultAsync(WatchFaultKind kind, char driveLetter)
    {
        bool Match(WatchFault fault) => fault.Kind == kind && fault.DriveLetter == char.ToUpperInvariant(driveLetter);
        lock (_faults)
        {
            if (_faults.FirstOrDefault(Match) is { } recorded)
            {
                return Task.FromResult(recorded);
            }

            var completion = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
            _faultWaiters.Add((Match, completion));
            return completion.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
        }
    }

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

    /// <summary>Makes the producer throw on this drive's next scan.</summary>
    public void FailNextProduction(char driveLetter, Exception failure)
    {
        _productionFailuresByDrive[char.ToUpperInvariant(driveLetter)] = failure;
    }

    /// <summary>
    ///     Parks this drive's next scan inside the producer until the returned gate is released.
    ///     The gate's <see cref="TestGate.Entered" /> completes once the scan has started.
    /// </summary>
    public TestGate HoldNextProduction(char driveLetter)
    {
        var gate = TrackGate();
        _productionHoldsByDrive[char.ToUpperInvariant(driveLetter)] = gate;
        return gate;
    }

    /// <summary>A gate the harness releases on teardown, so a failing test never wedges disposal.</summary>
    public TestGate TrackGate()
    {
        var gate = new TestGate();
        lock (_gates)
        {
            _gates.Add(gate);
        }

        return gate;
    }

    public static JournalBatch Batch(uint recordNumber, string fileName, long nextUsn = NextUsn + 100)
    {
        return new JournalBatch([Create(recordNumber, fileName)], JournalId, nextUsn);
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
        // gate would otherwise hang here instead of reporting.
        lock (_gates)
        {
            foreach (var gate in _gates)
            {
                gate.Release();
            }
        }

        Index.DisposeAsync().AsTask().WaitAsync(FakeIndexWatchSource.HangGuard).GetAwaiter().GetResult();
        foreach (var blockBuilder in _blockBuilders.Values)
        {
            blockBuilder.Dispose();
        }

        Directory.Delete(_cacheDirectory, recursive: true);
    }

    void RecordFault(WatchFault fault)
    {
        List<TaskCompletionSource<WatchFault>> matched = [];
        lock (_faults)
        {
            _faults.Add(fault);
            for (var index = _faultWaiters.Count - 1; index >= 0; index--)
            {
                if (_faultWaiters[index].Match(fault))
                {
                    matched.Add(_faultWaiters[index].Completion);
                    _faultWaiters.RemoveAt(index);
                }
            }
        }

        foreach (var completion in matched)
        {
            completion.TrySetResult(fault);
        }
    }

    async Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driveLetter = char.ToUpperInvariant(request.DriveLetter);
        if (_productionHoldsByDrive.TryRemove(driveLetter, out var hold))
        {
            hold.MarkEntered();
            await hold.WaitForReleaseAsync(cancellationToken);
        }

        if (_productionFailuresByDrive.TryRemove(driveLetter, out var productionFailure))
        {
            throw productionFailure;
        }

        var cursor = _cursorsByDrive[driveLetter];
        var block = _blockBuilders[driveLetter].OpenForReading(out var validation) ??
                    throw new InvalidOperationException($"Synthetic watch block was invalid: {validation}.");
        _producedBlocks[driveLetter] = block;
        return new MftBlockProduceResult(block, cursor.JournalId, cursor.NextUsn,
            SkippedRecordCount: 0, CompactionNeeded: false);
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
