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
public sealed class SyntheticIndexInspectionTests : IDisposable
{
    const uint Serial = 0x2345;

    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        File.WriteAllText(Path.Combine(TreeRoot, "readme.md"), "hello");
    }

    public void Dispose() => _fixture.Dispose();

    FileIndexOptions Options(string root, CacheTag cacheTag = default) =>
        _fixture.Options(volumeSerial: Serial) with
        {
            Drives = [new IndexedDrive('T', root, Serial, ProducerKind.Enumeration)],
            CacheTag = cacheTag
        };

    [TestMethod]
    public async Task ReadHeader_OfAReadyDrive_ReportsTheBlockTheIndexHolds()
    {
        var tag = new CacheTag("INSP", 2);
        await using var index = await FileIndex.OpenAsync(Options(TreeRoot, tag), Token);

        var header = SyntheticIndexInspection.ReadHeader(index, 't');

        Assert.IsNotNull(header);
        Assert.AreEqual(ProducerKind.Enumeration, header.ProducerKind);
        Assert.IsGreaterThanOrEqualTo(index.Drives.Single().Block!.LiveRowCount, header.RowCount, "slots include every live row");
        Assert.AreEqual(tag, header.CacheTag);
    }

    [TestMethod]
    public async Task ReadHeader_ForADriveWithNoBlock_IsNull()
    {
        await using var index = await FileIndex.OpenAsync(
            Options(Path.Combine(TreeRoot, "absent")), Token);

        Assert.AreEqual(DriveState.Offline, index.Drives.Single().State);
        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, 'T'));
    }

    [TestMethod]
    public async Task ReadHeader_ForAnUnconfiguredDrive_IsNull()
    {
        await using var index = await FileIndex.OpenAsync(Options(TreeRoot), Token);

        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, 'Q'));
    }

    [TestMethod]
    public async Task ReadHeader_IsACopyThatOutlivesTheIndex()
    {
        FileIndex index = await FileIndex.OpenAsync(Options(TreeRoot), Token);
        var header = SyntheticIndexInspection.ReadHeader(index, 'T');
        await index.DisposeAsync();

        Assert.IsNotNull(header);
        Assert.AreEqual(ProducerKind.Enumeration, header.ProducerKind);
        Assert.ThrowsExactly<ObjectDisposedException>(() => SyntheticIndexInspection.ReadHeader(index, 'T'));
    }

    [TestMethod]
    public void ReadHeader_OfNoIndex_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SyntheticIndexInspection.ReadHeader(null!, 'T'));
    }
}
