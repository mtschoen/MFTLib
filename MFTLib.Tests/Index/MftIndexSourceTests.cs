using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointLossFixture;
using static MFTLib.Tests.TestSupport.JournalWindows;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="MftIndexSource" /> carries the block producer and the watch source of an index as
///     one object. The journal read is swapped out through
///     <c>JournalIsolation.OverrideJournalWindow</c>, so these run on every platform.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MftIndexSourceTests : IDisposable
{
    public TestContext TestContext { get; set; } = null!;

    CheckpointLossFixture _fixture = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void Initialize() => _fixture = new CheckpointLossFixture(false, 'T');

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    public void Constructor_WithoutProducer_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new MftIndexSource(null!));
    }

    [TestMethod]
    public void Constructor_KeepsProducerAndWatchSourceTogether()
    {
        var watchSource = new ScriptedWatchSource();

        var source = new MftIndexSource(ProduceMftShapedBlock, watchSource.IndexSource);

        Assert.AreSame(watchSource.IndexSource, source.WatchSource);
        Assert.IsNull(new MftIndexSource(ProduceMftShapedBlock).WatchSource);
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    [DataRow(CacheMode.ExistingOnly)]
    public async Task NullSource_ColdOpenAndRescanReportWhyScanningCannotStart(CacheMode cacheMode)
    {
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(cacheMode: cacheMode) with { MftSource = null }, Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.IsNull(status.Block);
        if (cacheMode == CacheMode.ExistingOnly)
        {
            Assert.AreEqual(DriveFailureKind.CacheDeclined, status.Failure?.Kind);
        }
        else
        {
            Assert.AreEqual(DriveFailureKind.MftSourceMissing, status.Failure?.Kind);
            Assert.AreEqual("Drive T: no MFT source is available to scan this drive.",
                status.Failure?.Message);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.RescanAsync(['T'], Token));
            status = index.Drives.Single();
            Assert.AreEqual(DriveState.Failed, status.State);
            Assert.IsNull(status.Block);
            Assert.AreEqual(DriveFailureKind.MftSourceMissing, status.Failure?.Kind);
            Assert.AreEqual("Drive T: no MFT source is available to scan this drive.",
                status.Failure?.Message);
            Assert.IsNotNull(status.Failure);
            foreach (var identifier in new[] { "ProducerKind", "FileIndexOptions", "MftSource", "RescanAsync" })
            {
                Assert.IsFalse(status.Failure.Message.Contains(identifier, StringComparison.Ordinal));
            }
        }

        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], Token));
        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.ExistingOnly)]
    public async Task NullSource_WarmOpenAndFailedRescanKeepTheCachedBlock(CacheMode cacheMode)
    {
        await _fixture.Seed(cancellationToken: Token);
        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow>
        {
            ['T'] = HealthyWindow
        });
        var options = _fixture.Options(cacheMode: cacheMode) with { MftSource = null };
        await using (var index = await FileIndex.OpenAsync(options, Token))
        {
            var before = index.Drives.Single();
            Assert.AreEqual(DriveState.Ready, before.State);
            Assert.IsNull(before.Failure);
            Assert.IsNotNull(before.Block);
            Assert.AreEqual(BlockOrigin.WarmStartedFromCache, before.Block.Origin);
            var root = index.Root('T').RecordKey;
            await DriveResultAssertions.NotApplicableAsync(
                () => index.StartWatchingAsync(['T'], Token));
            Assert.IsFalse(index.Drives.Single().Watch.IsRequested);

            await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.RescanAsync(['T'], Token));
            var after = index.Drives.Single();
            Assert.AreEqual(DriveState.Ready, after.State);
            Assert.AreEqual(DriveFailureKind.MftSourceMissing, after.Failure?.Kind);
            Assert.AreEqual("Drive T: no MFT source is available to scan this drive.",
                after.Failure?.Message);
            Assert.IsNotNull(after.Block);
            Assert.AreEqual(before.Block.Origin, after.Block.Origin);
            Assert.AreEqual(before.Block.CacheLocation, after.Block.CacheLocation);
            Assert.AreEqual(before.Block.LiveRowCount, after.Block.LiveRowCount);
            Assert.AreEqual(root, index.Root('T').RecordKey);
            Assert.IsFalse(after.Watch.IsRequested);
        }

        await using var reopened = await FileIndex.OpenAsync(options, Token);
        Assert.AreEqual(DriveState.Ready, reopened.Drives.Single().State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives.Single().Block?.Origin);
        Assert.IsNull(reopened.Drives.Single().Failure);
    }

    [TestMethod]
    public async Task SourceWithoutWatchSource_ReportsWatchingNotApplicable()
    {
        await _fixture.Seed();
        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(_fixture.Options() with { MftSource = new MftIndexSource(ProduceMftShapedBlock) }, Token);

        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], Token));

        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
    }

    [TestMethod]
    public async Task SourceWithWatchSource_StartsTheWatchOnThatSource()
    {
        await _fixture.Seed();
        var watchSource = new ScriptedWatchSource();
        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options() with { MftSource = new MftIndexSource(ProduceMftShapedBlock, watchSource.IndexSource) }, Token);

        await index.StartWatchingAsync(['T'], Token);

        Assert.HasCount(1, watchSource.Starts);
        Assert.AreEqual('T', watchSource.Starts[0].Drive);
    }
}
