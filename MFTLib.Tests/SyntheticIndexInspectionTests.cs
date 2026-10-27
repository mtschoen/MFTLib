using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="SyntheticIndexInspection" /> reads the header of the block an open index holds, so a
///     consumer test can observe the producer and the slot count that <see cref="RootStatus" /> no
///     longer reports.
/// </summary>
[TestClass]
public sealed class SyntheticIndexInspectionTests : IndexTreeTestBase
{
    const uint Serial = 0x2345;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    protected override bool SeedDocuments => false;

    [TestInitialize]
    public void Initialize() => File.WriteAllText(Path.Combine(TreeRoot, "readme.md"), "hello");

    FileIndexOptions Options(string root, CacheTag cacheTag = default) =>
        Fixture.Options(volumeSerial: Serial) with
        {
            Root = SyntheticIndexedDrive.Create(new VolumeKey('T'), root, Serial, ProducerKind.Enumeration),
            CacheTag = cacheTag
        };

    [TestMethod]
    public async Task ReadHeader_OfAReadyDrive_ReportsTheBlockTheIndexHolds()
    {
        var tag = new CacheTag("INSP", 2);
        await using var index = await FileIndex.OpenAsync(Options(TreeRoot, tag), Token);

        var header = SyntheticIndexInspection.ReadHeader(index, new VolumeKey('t'));

        Assert.IsNotNull(header);
        Assert.AreEqual(ProducerKind.Enumeration, header.ProducerKind);
        Assert.IsGreaterThanOrEqualTo(index.Status.Block!.LiveRowCount, header.RowCount, "slots include every live row");
        Assert.AreEqual(tag, header.CacheTag);
    }

    [TestMethod]
    public async Task ReadHeader_ForADriveWithNoBlock_IsNull()
    {
        await using var index = await FileIndex.OpenAsync(
            Options(Path.Combine(TreeRoot, "absent")), Token);

        Assert.AreEqual(DriveState.Offline, index.Status.State);
        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, new VolumeKey('T')));
    }

    [TestMethod]
    public async Task ReadHeader_ForAnUnconfiguredDrive_IsNull()
    {
        await using var index = await FileIndex.OpenAsync(Options(TreeRoot), Token);

        Assert.IsNull(SyntheticIndexInspection.ReadHeader(index, new VolumeKey('Q')));
    }

    [TestMethod]
    public async Task ReadHeader_IsACopyThatOutlivesTheIndex()
    {
        var index = await FileIndex.OpenAsync(Options(TreeRoot), Token);
        var header = SyntheticIndexInspection.ReadHeader(index, new VolumeKey('T'));
        await index.DisposeAsync();

        Assert.IsNotNull(header);
        Assert.AreEqual(ProducerKind.Enumeration, header.ProducerKind);
        Assert.ThrowsExactly<ObjectDisposedException>(() => SyntheticIndexInspection.ReadHeader(index, new VolumeKey('T')));
    }

    [TestMethod]
    public void ReadHeader_OfNoIndex_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SyntheticIndexInspection.ReadHeader(null!, new VolumeKey('T')));
    }
}
