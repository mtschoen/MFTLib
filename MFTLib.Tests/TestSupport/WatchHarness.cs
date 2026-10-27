using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Stands one <see cref="FileIndex" /> per drive letter (<c>T</c>, <c>U</c> and <c>V</c> unless
///     told otherwise) up over synthetic MFT-shaped blocks, with a scripted producer and a
///     <see cref="ScriptedWatchSource" />, and tears them down again. It collects every
///     <see cref="FileIndex.Changed" /> change and every <see cref="FileIndex.WatchFaulted" /> fault.
/// </summary>
sealed class WatchHarness : IDisposable
{
    public const ulong JournalIdentifier = 7;
    public const long NextUsn = 100;

    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    readonly Dictionary<char, SyntheticBlockBuilder> _blockBuilders;
    readonly Dictionary<char, IndexWatchTarget> _cursorsByDrive;
    readonly Dictionary<char, FileIndex> _indicesByDrive = [];
    readonly ConcurrentDictionary<char, BlockFile> _producedBlocks = [];
    readonly ConcurrentDictionary<char, Exception> _productionFailuresByDrive = [];
    readonly ConcurrentDictionary<char, TestGate> _productionHoldsByDrive = [];
    readonly ConcurrentDictionary<char, ConcurrentQueue<WatchHarnessScan>> _scriptedScansByDrive = [];
    readonly ConcurrentDictionary<char, int> _productionCountsByDrive = [];
    readonly List<TestGate> _gates = [];
    readonly ConcurrentQueue<FileChange> _changes = [];
    readonly List<WatchFault> _faults = [];
    readonly FaultStatusLog _faultStatuses = new();
    readonly Dictionary<char, List<Task>> _recoveriesByDrive = [];
    readonly List<(Func<WatchFault, bool> Match, TaskCompletionSource<WatchFault> Completion)> _faultWaiters = [];
    readonly HashSet<char> _blocklessDrives;
    readonly TempDirectory _cache;

    public WatchHarness(params char[] driveLetters)
        : this(null, driveLetters)
    {
    }

    /// <summary>Stands the indices up over <paramref name="watchSource" />, which is null for the harness's own <see cref="Source" />.</summary>
    public WatchHarness(IIndexWatchSource? watchSource, params char[] driveLetters)
        : this(watchSource, [], driveLetters)
    {
    }

    WatchHarness(IIndexWatchSource? watchSource, HashSet<char> blocklessDrives, char[] driveLetters)
    {
        _blocklessDrives = blocklessDrives;
        if (driveLetters.Length == 0)
        {
            driveLetters = ['T', 'U', 'V'];
        }

        _cursorsByDrive = driveLetters.Select(char.ToUpperInvariant)
            .ToDictionary(letter => letter, letter => new IndexWatchTarget(letter, JournalIdentifier, NextUsn));
        _blockBuilders = _cursorsByDrive.ToDictionary(pair => pair.Key, pair => CreateMftBlock(pair.Value));
        _cache = new TempDirectory("mftlib-watch-cache");

        try
        {
            var mftSource = new MftIndexSource(Produce, watchSource ?? Source.IndexSource);
            foreach (var (letter, builder) in _blockBuilders)
            {
                var index = FileIndex.OpenAsync(new FileIndexOptions
                {
                    Root = SyntheticIndexedRoot.Create(new VolumeKey(letter), builder.DirectoryPath, builder.VolumeSerial),
                    CacheDirectory = CacheDirectory,
                    MftSource = mftSource
                }, CancellationToken.None).GetAwaiter().GetResult();
                index.Changed += _changes.Enqueue;
                index.WatchFaulted += fault => RecordFault(index, fault);
                _indicesByDrive[letter] = index;
            }

            Index = _indicesByDrive.Values.First();
        }
        catch
        {
            foreach (var index in _indicesByDrive.Values)
            {
                index.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            foreach (var blockBuilder in _blockBuilders.Values)
            {
                blockBuilder.Dispose();
            }

            _cache.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Stands the indices up with the producer failing every scan of <paramref name="blocklessDrives" />,
    ///     so those roots open <see cref="RootState.Failed" /> with no MFT-backed block.
    /// </summary>
    public static WatchHarness WithBlocklessRoots(char[] blocklessDrives, params char[] driveLetters) =>
        new(null, [.. blocklessDrives.Select(char.ToUpperInvariant)], driveLetters);

    public FileIndex Index { get; }

    public IReadOnlyCollection<FileIndex> Indices => _indicesByDrive.Values;

    public FileIndex IndexFor(char driveLetter) => _indicesByDrive[char.ToUpperInvariant(driveLetter)];

    /// <summary>The owned temporary directory this harness gives the indices as their cache directory.</summary>
    public string CacheDirectory => _cache.Path;

    public ScriptedWatchSource Source { get; } = new();

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

    public RootStatus RootFor(char driveLetter) => IndexFor(driveLetter).Status;

    /// <summary>Completes with the first fault of <paramref name="kind" /> on the root, recorded or future.</summary>
    public Task<WatchFault> WaitForFaultAsync(WatchFaultKind kind, char driveLetter)
    {
        bool Match(WatchFault fault) => fault.Kind == kind && fault.Volume == char.ToUpperInvariant(driveLetter);
        lock (_faults)
        {
            if (_faults.FirstOrDefault(Match) is { } recorded)
            {
                return Task.FromResult(recorded);
            }

            var completion = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
            _faultWaiters.Add((Match, completion));
            return completion.Task.WaitAsync(HangGuard);
        }
    }

    /// <summary>The cursor the producer stamps into the block it hands back on the next scan.</summary>
    public void SetNextProducedCursor(char driveLetter, ulong journalIdentifier, long nextUsn)
    {
        var normalizedLetter = char.ToUpperInvariant(driveLetter);
        _cursorsByDrive[normalizedLetter] = new IndexWatchTarget(normalizedLetter, journalIdentifier, nextUsn);
        _blockBuilders[normalizedLetter].MutateHeader((ref header) =>
        {
            header.UsnJournalIdentifier = journalIdentifier;
            header.UsnNextUsn = nextUsn;
        });
    }

    /// <summary>Makes the producer throw on this root's next scan.</summary>
    public void FailNextProduction(char driveLetter, Exception failure)
    {
        _productionFailuresByDrive[char.ToUpperInvariant(driveLetter)] = failure;
    }

    /// <summary>
    ///     Scripts this root's next scans, one entry per scan in order. Scans past the script
    ///     return a block whose catch-up held.
    /// </summary>
    public void ScriptScans(char driveLetter, params WatchHarnessScan[] scans)
    {
        var queue = _scriptedScansByDrive.GetOrAdd(char.ToUpperInvariant(driveLetter), _ => []);
        foreach (var scan in scans)
        {
            queue.Enqueue(scan);
        }
    }

    /// <summary>How many times the producer has been called for this root, the open's scan included.</summary>
    public int ProductionCount(char driveLetter) =>
        _productionCountsByDrive.GetValueOrDefault(char.ToUpperInvariant(driveLetter));

    /// <summary>
    ///     Parks this root's next scan inside the producer until the returned gate is released.
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

    /// <summary>
    ///     Parks the first pump apply on <paramref name="driveLetter" /> inside the apply, before
    ///     its gate, until the returned gate is released. Later applies pass straight through.
    /// </summary>
    public TestGate HoldFirstApply(char driveLetter)
    {
        var gate = TrackGate();
        var held = 0;
        IndexFor(driveLetter).Hooks.ApplyJournalEntriesEntered = letter =>
        {
            if (letter == new VolumeKey(driveLetter) && Interlocked.Exchange(ref held, 1) == 0)
            {
                gate.MarkEntered();
                gate.WaitForRelease();
            }
        };
        return gate;
    }

    public static JournalBatch Batch(uint recordNumber, string fileName, long nextUsn = NextUsn + 100)
    {
        return new JournalBatch([Create(recordNumber, fileName)], JournalIdentifier, nextUsn);
    }

    public static UsnJournalEntry Create(uint recordNumber, string fileName, ulong parentRecordNumber = 5)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            SequenceNumber = 1,
            Usn = 1,
            TimestampUtc = ChangeMoment,
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    public void Dispose()
    {
        lock (_gates)
        {
            foreach (var gate in _gates)
            {
                gate.Release();
            }
        }

        foreach (var index in _indicesByDrive.Values)
        {
            index.DisposeAsync().AsTask().WaitAsync(HangGuard).GetAwaiter().GetResult();
        }

        foreach (var blockBuilder in _blockBuilders.Values)
        {
            blockBuilder.Dispose();
        }

        _cache.Dispose();
    }

    /// <summary>
    ///     Completes when the root's latest recovery has ended.
    /// </summary>
    public Task WaitForRecoveryAsync(char driveLetter)
    {
        lock (_faults)
        {
            return _recoveriesByDrive.TryGetValue(char.ToUpperInvariant(driveLetter), out var recoveries)
                ? recoveries[^1].WaitAsync(HangGuard)
                : throw new InvalidOperationException($"No recovery of root {driveLetter} was queued.");
        }
    }

    /// <summary>
    ///     The root's latest recovery's own completion, the task the index's disposal waits for,
    ///     unwrapped.
    /// </summary>
    public Task RecoveryCompletion(char driveLetter)
    {
        lock (_faults)
        {
            return _recoveriesByDrive.TryGetValue(char.ToUpperInvariant(driveLetter), out var recoveries)
                ? recoveries[^1]
                : throw new InvalidOperationException($"No recovery of root {driveLetter} was queued.");
        }
    }

    /// <summary>How many distinct recoveries of the root were queued when one of its faults was raised.</summary>
    public int RecoveryCount(char driveLetter)
    {
        lock (_faults)
        {
            return _recoveriesByDrive.TryGetValue(char.ToUpperInvariant(driveLetter), out var recoveries)
                ? recoveries.Count
                : 0;
        }
    }

    /// <summary>The status of <paramref name="driveLetter" /> at each of its lost-catch-up faults, in raise order.</summary>
    public RootStatus[] CatchUpLossStatuses(char driveLetter) => _faultStatuses.CatchUpLossStatuses(driveLetter);

    void RecordFault(FileIndex index, WatchFault fault)
    {
        _faultStatuses.Record(index, fault);
        List<TaskCompletionSource<WatchFault>> matched = [];
        _ = index.TryGetRecoveryCompletionForTest(new VolumeKey((fault.Volume).Letter), out var recovery);
        lock (_faults)
        {
            _faults.Add(fault);
            if (recovery is not null)
            {
                var recoveries = _recoveriesByDrive.GetValueOrDefault(fault.Volume.Letter) ?? [];
                _recoveriesByDrive[(fault.Volume).Letter] = recoveries;
                if (!recoveries.Contains(recovery))
                {
                    recoveries.Add(recovery);
                }
            }

            for (var waiterIndex = _faultWaiters.Count - 1; waiterIndex >= 0; waiterIndex--)
            {
                if (_faultWaiters[waiterIndex].Match(fault))
                {
                    matched.Add(_faultWaiters[waiterIndex].Completion);
                    _faultWaiters.RemoveAt(waiterIndex);
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
        var driveLetter = char.ToUpperInvariant((request.Volume).Letter);
        _productionCountsByDrive.AddOrUpdate(driveLetter, 1, (_, count) => count + 1);
        if (_blocklessDrives.Contains(driveLetter))
        {
            throw new IOException($"Root {driveLetter} cannot be scanned.");
        }

        if (_productionHoldsByDrive.TryRemove(driveLetter, out var hold))
        {
            hold.MarkEntered();
            await hold.WaitForReleaseAsync(cancellationToken);
        }

        if (_productionFailuresByDrive.TryRemove(driveLetter, out var productionFailure))
        {
            throw productionFailure;
        }

        var scripted = _scriptedScansByDrive.TryGetValue(driveLetter, out var scans) &&
                       scans.TryDequeue(out var scan)
            ? scan
            : new WatchHarnessScan();
        if (scripted.Hold is { } scriptedHold)
        {
            scriptedHold.MarkEntered();
            await scriptedHold.WaitForReleaseAsync(cancellationToken);
        }

        if (scripted.Failure is { } scriptedFailure)
        {
            throw scriptedFailure;
        }

        var block = _blockBuilders[driveLetter].OpenForReading(out var validation) ??
                    throw new InvalidOperationException($"Synthetic watch block was invalid: {validation}.");
        _producedBlocks[driveLetter] = block;
        return new MftBlockProduceResult(block, SkippedRecordCount: 0)
        {
            CatchUpLoss = scripted.CatchUpLoss
        };
    }

    static SyntheticBlockBuilder CreateMftBlock(IndexWatchTarget cursor)
    {
        var builder = SyntheticBlockBuilder.MftShaped();
        builder.MutateHeader((ref header) =>
        {
            header.UsnJournalIdentifier = cursor.JournalIdentifier;
            header.UsnNextUsn = cursor.NextUsn;
        });
        return builder;
    }
}

/// <summary>
///     One scripted scan of a <see cref="WatchHarness" /> root: parked on <paramref name="Hold" />
///     when set, then throwing <paramref name="Failure" /> when set, and otherwise returning a block
///     whose catch-up <paramref name="CatchUpLoss" /> says was lost, or held when it is null. A
///     hold gate should come from <see cref="WatchHarness.TrackGate" />, so teardown releases it.
/// </summary>
sealed record WatchHarnessScan(
    JournalCheckpointLoss? CatchUpLoss = null,
    Exception? Failure = null,
    TestGate? Hold = null);
