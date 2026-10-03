using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using static MFTLib.Tests.TestSupport.CheckpointCacheTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Seeds a cache block for drive <c>T</c> while the journal still holds its checkpoint, then
///     opens it cache-only after the journal has trimmed that checkpoint away, which is how a
///     cache-only open comes to adopt an unresumable block. The journal read is swapped out
///     through <c>JournalCheckpointCheck.OverrideJournalForTest</c>, a process-wide override, so a
///     class using this carries <c>[DoNotParallelize]</c>.
/// </summary>
internal sealed class CacheOnlyUnresumableFixture : IDisposable
{
    readonly OwnedIndexDirectories _directories = new();
    readonly string _treeRoot;
    readonly string _cacheDirectory;

    public CacheOnlyUnresumableFixture()
    {
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(_treeRoot);
    }

    public FakeIndexWatchSource Source { get; } = new();

    public async Task<FileIndex> OpenAdoptingAnUnresumableBlockAsync(CancellationToken cancellationToken)
    {
        using (Journal(firstUsn: 0))
        {
            await using var seeding = await FileIndex.OpenAsync(Options(cacheOnly: false), cancellationToken);
        }

        using (Journal(firstUsn: CachedNextUsn + 500))
        {
            return await FileIndex.OpenAsync(Options(cacheOnly: true), cancellationToken);
        }
    }

    public void Dispose()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(bool cacheOnly)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = ProduceMftShapedBlock,
            WatchSource = Source,
            InitialOpenCacheOnly = cacheOnly
        };
    }

    static IDisposable Journal(long firstUsn)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(_ =>
            new JournalWindow(CachedJournalId, firstUsn, CachedNextUsn + 4_000, 64, 128L * 1024 * 1024));
    }

}
