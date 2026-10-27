using MFTLib.Tests.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>Owns one tree and deferred cache directory for each test instance.</summary>
[TestClass]
public abstract class IndexTreeTestBase : IDisposable
{
    private IndexTreeFixture? _fixture;

    private protected IndexTreeFixture Fixture => _fixture!;

    protected string TreeRoot => Fixture.TreeRoot;
    protected string CacheDirectoryPath => Fixture.CacheDirectory;
    protected uint UniqueVolumeSerial { get; private set; }
    protected virtual bool SeedDocuments => true;

    [TestInitialize]
    public void InitializeIndexTree()
    {
        UniqueVolumeSerial = TestVolumeSerial.GetNext();
        _fixture = new IndexTreeFixture(SeedDocuments);
    }

    public virtual void Dispose()
    {
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }
}
