using MFTLib.Index;

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

    public IndexedDrive Drive(char drive = 'T', uint volumeSerial = 0x0BADF00D,
        ProducerKind producer = ProducerKind.Enumeration) =>
        new(new VolumeKey(drive), TreeRoot, volumeSerial, producer);

    public FileIndexOptions Options(char drive = 'T', uint volumeSerial = 0x0BADF00D,
        ProducerKind producer = ProducerKind.Enumeration, CacheMode? cacheMode = null) => new()
        {
            Drives = [Drive(drive, volumeSerial, producer)],
            CacheDirectory = CacheDirectory,
            CacheMode = cacheMode
        };

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
