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

    public IndexedDrive Drive(char driveLetter = 'T')
    {
        var key = new VolumeKey(char.ToUpperInvariant(driveLetter));
        var fixture = _trees[key];
        var serial = key.Letter == 'U' ? 0x0BADBEEFu : 0x0BADF00Du;
        return fixture.Drive(key.Letter, serial, producer: ProducerKind.Mft);
    }

    public FileIndexOptions Options(MftBlockProducer? producer = null,
        IIndexWatchSource? watchSource = null, CacheMode cacheMode = CacheMode.Persistent,
        IReadOnlyList<IndexedDrive>? drives = null)
    {
        return new FileIndexOptions
        {
            Drives = drives ?? _trees.Keys.Select(key => Drive(key.Letter)).ToArray(),
            CacheDirectory = CacheDirectory,
            CacheMode = cacheMode,
            MftSource = new MftIndexSource(producer ?? ProduceMftShapedBlock, watchSource)
        };
    }

    static VolumeKey VolumeOf(RootKey root) =>
        root.TryGetVolume(out var volume) ? volume : throw new InvalidOperationException($"Journal windows are volume-only, got {root}.");

    public async Task Seed(FileIndexOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= Options();
        var drives = options.Drives.ToArray();
        using var journals = JournalWindows.ForDrives(
            drives.ToDictionary(drive => VolumeOf(drive.Drive), _ => HealthyWindow));
        await using var index = await FileIndex.OpenAsync(options with { Drives = drives }, cancellationToken);
        foreach (var drive in index.Drives)
        {
            Assert.AreEqual(BlockOrigin.ProducedByScan, drive.Block!.Origin);
            Assert.IsNull(drive.Watch.CheckpointLoss);
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
