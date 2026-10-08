using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="SyntheticIndexInspection" /> reads the header of the block an open index holds, so a
///     consumer test can observe the producer and the slot count that <see cref="DriveStatus" /> no
///     longer reports.
/// </summary>
[TestClass]
public class SyntheticIndexInspectionTests
{
    const uint Serial = 0x2345;

    OwnedIndexDirectories _directories = null!;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        Directory.CreateDirectory(_directories.TreeRoot);
        File.WriteAllText(Path.Combine(_directories.TreeRoot, "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(string root, CacheTag cacheTag = default) => new()
    {
        Drives = [new IndexedDrive('T', root, Serial)],
        CacheDirectory = _directories.CacheDirectory,
        ProducerPolicy = ProducerPolicy.Enumeration,
        CacheTag = cacheTag
    };

    [TestMethod]
    public async Task ReadHeader_OfAReadyDrive_ReportsTheBlockTheIndexHolds()
    {
        var tag = new CacheTag("INSP", 2);
        await using var index = await FileIndex.OpenAsync(Options(_directories.TreeRoot, tag), Token);

        var header = SyntheticIndexInspection.ReadHeader(index, 't');

        Assert.IsNotNull(header);
        Assert.AreEqual(ProducerKind.Enumeration, header.ProducerKind);
        Assert.IsTrue(header.RowCount >= index.Drives.Single().Block.LiveRowCount, "slots include every live row");
        Assert.AreEqual(tag, header.CacheTag);
    }

    [TestMethod]
    public async Task ReadHeader_ForADriveWithNoBlock_IsNull()
    {
        await using var index = await FileIndex.OpenAsync(
            Options(Path.Combine(_directories.TreeRoot, "absent")), Token);

        Assert.AreEqual(DriveState.Offline, index.Drives.Single().State);
        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, 'T'));
    }

    [TestMethod]
    public async Task ReadHeader_ForAnUnconfiguredDrive_IsNull()
    {
        await using var index = await FileIndex.OpenAsync(Options(_directories.TreeRoot), Token);

        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, 'Q'));
    }

    [TestMethod]
    public async Task ReadHeader_IsACopyThatOutlivesTheIndex()
    {
        FileIndex index = await FileIndex.OpenAsync(Options(_directories.TreeRoot), Token);
        var header = SyntheticIndexInspection.ReadHeader(index, 'T');
        await index.DisposeAsync();

        Assert.IsNotNull(header);
        Assert.AreEqual(ProducerKind.Enumeration, header.ProducerKind);
        Assert.ThrowsException<ObjectDisposedException>(() => SyntheticIndexInspection.ReadHeader(index, 'T'));
    }

    [TestMethod]
    public void ReadHeader_OfNoIndex_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => SyntheticIndexInspection.ReadHeader(null!, 'T'));
    }
}
