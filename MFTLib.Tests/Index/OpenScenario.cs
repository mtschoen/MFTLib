using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A set of MFT drives over one owned tree and cache directory whose producer parks on a
///     per-drive <see cref="TestGate" /> when the test holds one, throws the failure set for its
///     drive, and otherwise writes a real block at the requested path with the next queued catch-up
///     loss attached. Every report <see cref="FileIndexOptions.OpenProgress" /> delivers is kept
///     and signalled, so a test orders itself against a drive settling without sleeping.
/// </summary>
internal sealed class OpenScenario : IDisposable
{
    readonly ConcurrentDictionary<char, TestGate> _gates = [];
    readonly ConcurrentDictionary<char, int> _productionsBeforeHold = [];
    readonly ConcurrentDictionary<char, Exception> _failures = [];
    readonly ConcurrentDictionary<char, ConcurrentQueue<JournalCheckpointLoss>> _losses = [];
    readonly ConcurrentDictionary<char, int> _productions = [];
    readonly ConcurrentQueue<IndexDriveOpened> _reports = [];
    readonly SemaphoreSlim _reportSignal = new(0);
    readonly string _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
    int _activeProducers;

    public OpenScenario()
    {
        Directory.CreateDirectory(_treeRoot);
        CacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
    }

    public string CacheDirectory { get; }

    public FakeIndexWatchSource Source { get; } = new();

    /// <summary>Producers currently between entering and leaving, so a test can prove none is left running.</summary>
    public int ActiveProducers => Volatile.Read(ref _activeProducers);

    public IReadOnlyCollection<IndexDriveOpened> Reports => _reports;

    /// <summary>Runs inside the progress callback after the report is kept and signalled; may park or throw.</summary>
    public Action<IndexDriveOpened>? OnReport { get; set; }

    public static JournalCheckpointLoss Loss(char driveLetter) => new()
    {
        DriveLetter = driveLetter,
        DetectedDuring = JournalCheckpointLossDetection.ScanCatchUp,
        Cause = JournalCheckpointLossCause.CheckpointTrimmed,
        CheckpointUsn = 1000,
        FirstUsn = 5000,
        NextUsn = 9000,
        AllocationDelta = 4096,
        MaximumSize = 32768,
        BytesBehind = 4000,
        SizeThatWouldHaveRetained = 12288
    };

    /// <summary>Parks every production of the drive until the returned gate is released.</summary>
    public TestGate Hold(char driveLetter)
    {
        var gate = new TestGate();
        _gates[driveLetter] = gate;
        return gate;
    }

    /// <summary>Lets the drive's first <paramref name="productions" /> productions through before its held gate applies.</summary>
    public void LetProductionsThrough(char driveLetter, int productions) =>
        _productionsBeforeHold[driveLetter] = productions;

    public void Fail(char driveLetter, Exception failure) => _failures[driveLetter] = failure;

    /// <summary>Queues a lost catch-up for the drive's next production.</summary>
    public void ScriptLoss(char driveLetter) =>
        _losses.GetOrAdd(driveLetter, _ => new ConcurrentQueue<JournalCheckpointLoss>()).Enqueue(Loss(driveLetter));

    public int ProductionsOf(char driveLetter) => _productions.GetValueOrDefault(driveLetter);

    public IEnumerable<IndexDriveOpened> ReportsFor(char driveLetter) =>
        _reports.Where(report => report.DriveLetter == driveLetter);

    /// <summary>Forgets every report so far, for a test that opens the index more than once.</summary>
    public void ResetReports()
    {
        _reports.Clear();
        while (_reportSignal.Wait(0))
        {
        }
    }

    /// <summary>Completes when one more report has arrived than this method has already consumed.</summary>
    public async Task WaitForReportAsync()
    {
        if (!await _reportSignal.WaitAsync(FakeIndexWatchSource.HangGuard))
        {
            throw new TimeoutException("No further OpenProgress report arrived.");
        }
    }

    /// <summary>The names of the cache directory's block files, without their owner-lock siblings.</summary>
    public string[] BlockFileNames() => !Directory.Exists(CacheDirectory)
        ? []
        : Directory.GetFiles(CacheDirectory)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();

    public static string CanonicalBlockName(char driveLetter) =>
        MFTLib.Index.CacheDirectory.BlockFileName(driveLetter, SerialOf(driveLetter));

    public string CanonicalBlockPath(char driveLetter) => Path.Combine(CacheDirectory, CanonicalBlockName(driveLetter));

    public FileIndexOptions Options(string driveLetters, bool cacheOnly = false) => new()
    {
        Drives = driveLetters.Select(letter => new IndexedDrive(letter, _treeRoot, SerialOf(letter))).ToArray(),
        CacheDirectory = CacheDirectory,
        ProducerPolicy = ProducerPolicy.Mft,
        MftProducer = ProduceAsync,
        WatchSource = Source,
        InitialOpenCacheOnly = cacheOnly,
        OpenProgress = new SynchronousProgress<IndexDriveOpened>(report =>
        {
            _reports.Enqueue(report);
            _reportSignal.Release();
            OnReport?.Invoke(report);
        })
    };

    public void Dispose()
    {
        foreach (var directory in new[] { _treeRoot, CacheDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows, and a directory the
                // scenario never created is already gone.
            }
        }
    }

    static uint SerialOf(char driveLetter) => driveLetter;

    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var driveLetter = char.ToUpperInvariant(request.DriveLetter);
        Interlocked.Increment(ref _activeProducers);
        try
        {
            var production = _productions.AddOrUpdate(driveLetter, 1, (_, count) => count + 1);
            if (_gates.TryGetValue(driveLetter, out var gate) &&
                production > _productionsBeforeHold.GetValueOrDefault(driveLetter))
            {
                gate.MarkEntered();
                await gate.WaitForReleaseAsync(cancellationToken).WaitAsync(FakeIndexWatchSource.HangGuard, cancellationToken);
            }

            if (_failures.TryGetValue(driveLetter, out var failure))
            {
                throw failure;
            }

            FileIndexWatchRescanTests.WriteMftShapedBlock(request.BlockPath, request.VolumeSerial,
                journalId: 7, nextUsn: 4096);
            var loss = _losses.TryGetValue(driveLetter, out var queue) && queue.TryDequeue(out var scripted)
                ? scripted
                : null;
            return new MftBlockProduceResult(BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false)
            {
                CatchUpLoss = loss
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeProducers);
        }
    }
}
