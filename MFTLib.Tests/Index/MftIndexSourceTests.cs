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

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.IsNull(status.Block);
        if (cacheMode == CacheMode.ExistingOnly)
        {
            Assert.AreEqual(RootFailureKind.CacheDeclined, status.Failure?.Kind);
        }
        else
        {
            Assert.AreEqual(RootFailureKind.MftUnavailable, status.Failure?.Kind);
            Assert.AreEqual("Volume T: no MFT source is available to scan this volume.",
                status.Failure?.Message);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await RootResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.RescanAsync(Token));
            status = index.Status;
            Assert.AreEqual(RootState.Failed, status.State);
            Assert.IsNull(status.Block);
            Assert.AreEqual(RootFailureKind.MftUnavailable, status.Failure?.Kind);
            Assert.AreEqual("Volume T: no MFT source is available to scan this volume.",
                status.Failure?.Message);
            Assert.IsNotNull(status.Failure);
            foreach (var identifier in new[] { "ProducerKind", "FileIndexOptions", "MftSource", "RescanAsync" })
            {
                Assert.IsFalse(status.Failure.Message.Contains(identifier, StringComparison.Ordinal));
            }
        }

        await RootResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(Token));
        Assert.IsFalse(index.Status.Watch.IsRequested);
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.ExistingOnly)]
    public async Task NullSource_WarmOpenAndFailedRescanKeepTheCachedBlock(CacheMode cacheMode)
    {
        await _fixture.Seed(cancellationToken: Token);
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow>
        {
            ['T'] = HealthyWindow
        });
        var options = _fixture.Options(cacheMode: cacheMode) with { MftSource = null };
        await using (var index = await FileIndex.OpenAsync(options, Token))
        {
            var before = index.Status;
            Assert.AreEqual(RootState.Ready, before.State);
            Assert.IsNull(before.Failure);
            Assert.IsNotNull(before.Block);
            Assert.AreEqual(BlockOrigin.WarmStartedFromCache, before.Block.Origin);
            var root = index.Root(TestContext.CancellationToken).RecordKey;
            await RootResultAssertions.NotApplicableAsync(
                () => index.StartWatchingAsync(Token));
            Assert.IsFalse(index.Status.Watch.IsRequested);

            await RootResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.RescanAsync(Token));
            var after = index.Status;
            Assert.AreEqual(RootState.Ready, after.State);
            Assert.AreEqual(RootFailureKind.MftUnavailable, after.Failure?.Kind);
            Assert.AreEqual("Volume T: no MFT source is available to scan this volume.",
                after.Failure?.Message);
            Assert.IsNotNull(after.Block);
            Assert.AreEqual(before.Block.Origin, after.Block.Origin);
            Assert.AreEqual(before.Block.CacheLocation, after.Block.CacheLocation);
            Assert.AreEqual(before.Block.LiveRowCount, after.Block.LiveRowCount);
            Assert.AreEqual(root, index.Root(TestContext.CancellationToken).RecordKey);
            Assert.IsFalse(after.Watch.IsRequested);
        }

        await using var reopened = await FileIndex.OpenAsync(options, Token);
        Assert.AreEqual(RootState.Ready, reopened.Status.State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block?.Origin);
        Assert.IsNull(reopened.Status.Failure);
    }

    [TestMethod]
    public async Task SourceWithoutWatchSource_ReportsWatchingNotApplicable()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(_fixture.Options() with { MftSource = new MftIndexSource(ProduceMftShapedBlock) }, Token);

        await RootResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(Token));

        Assert.IsFalse(index.Status.Watch.IsRequested);
    }

    [TestMethod]
    public async Task SourceWithWatchSource_StartsTheWatchOnThatSource()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        var watchSource = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options() with { MftSource = new MftIndexSource(ProduceMftShapedBlock, watchSource.IndexSource) }, Token);

        await index.StartWatchingAsync(Token);

        Assert.HasCount(1, watchSource.Starts);
        Assert.AreEqual('T', watchSource.Starts[0].Volume);
    }
}
