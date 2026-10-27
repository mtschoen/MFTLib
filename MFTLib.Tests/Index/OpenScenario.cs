using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;

namespace MFTLib.Tests.Index;

/// <summary>
///     A set of MFT roots over one owned tree and cache directory whose producer parks on a
///     per-drive <see cref="TestGate" /> when the test holds one, throws the failure set for its
///     drive, and otherwise writes a real block at the requested path with the next queued catch-up
///     loss attached.
/// </summary>
sealed class OpenScenario : IDisposable
{
    readonly ConcurrentDictionary<char, TestGate> _gates = [];
    readonly ConcurrentDictionary<char, int> _productionsBeforeHold = [];
    readonly ConcurrentDictionary<char, Exception> _failures = [];
    readonly ConcurrentDictionary<char, ConcurrentQueue<JournalCheckpointLoss>> _losses = [];
    readonly ConcurrentDictionary<char, int> _productions = [];
    readonly IndexTreeFixture _fixture = new(seedDocuments: false);
    string TreeRoot => _fixture.TreeRoot;
    int _activeProducers;

    public string CacheDirectory => _fixture.CacheDirectory;

    public ScriptedWatchSource Source { get; } = new();

    /// <summary>Producers currently between entering and leaving, so a test can prove none is left running.</summary>
    public int ActiveProducers => Volatile.Read(ref _activeProducers);

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
        _losses.GetOrAdd(driveLetter, _ => new ConcurrentQueue<JournalCheckpointLoss>()).Enqueue(CatchUpLossTestSupport.StandardCatchUpLoss(driveLetter));

    public int ProductionsOf(char driveLetter) => _productions.GetValueOrDefault(driveLetter);

    /// <summary>The names of the cache directory's block files, without their owner-lock siblings.</summary>
    public string[] BlockFileNames() => !Directory.Exists(CacheDirectory)
        ? []
        : [.. Directory.GetFiles(CacheDirectory)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    public static string CanonicalBlockName(char driveLetter) =>
        MFTLib.Index.CacheDirectory.BlockFileName(new VolumeKey(driveLetter), SerialOf(driveLetter));

    public string CanonicalBlockPath(char driveLetter) => Path.Combine(CacheDirectory, CanonicalBlockName(driveLetter));

    public FileIndexOptions Options(char driveLetter, bool cacheOnly = false) => new()
    {
        Root = SyntheticIndexedDrive.Create(new VolumeKey(driveLetter), TreeRoot, SerialOf(driveLetter)),
        CacheDirectory = CacheDirectory,
        MftSource = new MftIndexSource(ProduceAsync, Source.IndexSource),
        CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent
    };

    public void Dispose() => _fixture.Dispose();

    static uint SerialOf(char driveLetter) => driveLetter;

    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var driveLetter = char.ToUpperInvariant((request.Drive).Letter);
        Interlocked.Increment(ref _activeProducers);
        try
        {
            var production = _productions.AddOrUpdate(driveLetter, 1, (_, count) => count + 1);
            if (_gates.TryGetValue(driveLetter, out var gate) &&
                production > _productionsBeforeHold.GetValueOrDefault(driveLetter))
            {
                gate.MarkEntered();
                await gate.WaitForReleaseAsync(cancellationToken).WaitAsync(HangGuard, cancellationToken);
            }

            if (_failures.TryGetValue(driveLetter, out var failure))
            {
                throw failure;
            }

            SeededBlocks.Write(request.BlockPath, request.VolumeSerial,
                journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
            var loss = _losses.TryGetValue(driveLetter, out var queue) && queue.TryDequeue(out var scripted)
                ? scripted
                : null;
            return new MftBlockProduceResult(BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                SkippedRecordCount: 0)
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
