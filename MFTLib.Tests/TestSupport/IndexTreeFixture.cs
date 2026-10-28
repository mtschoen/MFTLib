using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

sealed class IndexTreeFixture : IDisposable
{
    readonly TempDirectory _tree;
    readonly TempDirectory _cache;

    public string TreeRoot => _tree.Path;

    public string CacheDirectory => _cache.Path;

    public IndexTreeFixture(bool seedDocuments = true)
    {
        _tree = new TempDirectory("mftlib-tree");
        try
        {
            _cache = new TempDirectory("mftlib-cache", create: false);
        }
        catch
        {
            _tree.Dispose();
            throw;
        }

        try
        {
            if (seedDocuments)
            {
                var documents = Path.Combine(TreeRoot, "Documents");
                Directory.CreateDirectory(documents);
                File.WriteAllText(Path.Combine(documents, "readme.md"), "hello");
            }
        }
        catch
        {
            _tree.Dispose();
            _cache.Dispose();
            throw;
        }
    }

    /// <summary>Options that open the drive letter as a synthetic volume rooted at <see cref="TreeRoot" />.</summary>
    public FileIndexOptions Options(char drive = 'T', uint volumeSerial = 0x0BADF00D,
        ProducerSelection producer = ProducerSelection.WalkOnly, CacheMode? cacheMode = null) =>
        SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = new VolumeKey(drive),
            Producer = producer,
            CacheDirectory = CacheDirectory,
            CacheMode = cacheMode
        }, volumeSerial, volume: new VolumeKey(drive), volumeRootDirectory: TreeRoot);

    public void Dispose()
    {
        try
        {
            _cache.Dispose();
        }
        finally
        {
            _tree.Dispose();
        }
    }
}
