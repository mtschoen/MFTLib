using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

sealed class CheckpointLossFixture : IDisposable
{
    public const ulong CachedJournalIdentifier = 0xABCD;
    public const long CachedNextUsn = 1_000_000;
    public static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    readonly Dictionary<VolumeKey, IndexTreeFixture> _trees = [];

    public CheckpointLossFixture(params char[] driveLetters) : this(true, driveLetters)
    {
    }

    public CheckpointLossFixture(bool seedDocuments, params char[] driveLetters)
    {
        var letters = driveLetters is { Length: > 0 } ? driveLetters : ['T'];
        try
        {
            foreach (var letter in letters)
            {
                var key = new VolumeKey(char.ToUpperInvariant(letter));
                _trees[key] = new IndexTreeFixture(seedDocuments);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string CacheDirectory => _trees.First().Value.CacheDirectory;

    public IndexedRoot Root(char driveLetter = 'T')
    {
        var key = new VolumeKey(char.ToUpperInvariant(driveLetter));
        var fixture = _trees[key];
        var serial = key.Letter == 'U' ? 0x0BADBEEFu : 0x0BADF00Du;
        return fixture.Root(key.Letter, serial, producer: ProducerKind.Mft);
    }

    public FileIndexOptions Options(MftBlockProducer? producer = null,
        IIndexWatchSource? watchSource = null, CacheMode cacheMode = CacheMode.Persistent,
        IndexedRoot? root = null, char driveLetter = 'T')
    {
        return new FileIndexOptions
        {
            Root = root ?? Root(driveLetter),
            CacheDirectory = CacheDirectory,
            CacheMode = cacheMode,
            MftSource = new MftIndexSource(producer ?? ProduceMftShapedBlock, watchSource)
        };
    }

    static VolumeKey VolumeOf(RootKey root) =>
        root.TryGetVolume(out var volume) ? volume : throw new InvalidOperationException($"Journal windows are volume-only, got {root}.");

    public async Task Seed(FileIndexOptions? options = null, CancellationToken cancellationToken = default)
    {
        var roots = options?.Root is { } explicitRoot
            ? [explicitRoot]
            : _trees.Keys.Select(key => Root(key.Letter)).ToArray();
        var baseOptions = options ?? Options();
        using var journals = JournalWindows.ForVolumes(
            roots.ToDictionary(root => VolumeOf(root.Root), _ => HealthyWindow));
        foreach (var root in roots)
        {
            await using var index = await FileIndex.OpenAsync(baseOptions with { Root = root }, cancellationToken);
            var status = index.Status;
            Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
            Assert.IsNull(status.Watch.CheckpointLoss);
        }
    }

    public async Task<FileIndex> OpenAdoptingAnUnresumableBlockAsync(
        ScriptedWatchSource source, CancellationToken cancellationToken)
    {
        await Seed(Options(watchSource: source.IndexSource), cancellationToken);
        using var journal = JournalWindows.Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: CachedNextUsn + 4_000);
        return await FileIndex.OpenAsync(Options(watchSource: source.IndexSource,
            cacheMode: CacheMode.ExistingOnly), cancellationToken);
    }

    public static Task<MftBlockProduceResult> ProduceMftShapedBlock(
        MftBlockProduceRequest request, CancellationToken cancellationToken) =>
        SeededBlocks.Produce(request, CachedJournalIdentifier, CachedNextUsn, FixedMoment);

    public static SyntheticJournalWindow HealthyWindow =>
        JournalWindows.Window(CachedJournalIdentifier, 0, CachedNextUsn);

    public static SyntheticJournalWindow TrimmedWindow =>
        JournalWindows.Window(CachedJournalIdentifier, CachedNextUsn + 500, CachedNextUsn + 4_000);

    public void Dispose()
    {
        Exception? firstException = null;
        foreach (var tree in _trees.Values)
        {
            try
            {
                tree.Dispose();
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstException).Throw();
        }
    }
}
