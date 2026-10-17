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
    const string Reason = "the broker is not running";

    public TestContext TestContext { get; set; } = null!;

    CheckpointLossFixture _fixture = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

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
    [DataRow("")]
    [DataRow("   ")]
    public void Unavailable_WithBlankReason_Throws(string reason)
    {
        Assert.ThrowsExactly<ArgumentException>(() => MftIndexSources.Unavailable(reason));
    }

    [TestMethod]
    public void Unavailable_WithNullReason_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MftIndexSources.Unavailable(null!));
    }

    [TestMethod]
    public async Task Unavailable_FailsEachScanWithTheDriveLetterAndTheReason()
    {
        var source = MftIndexSources.Unavailable(Reason);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => source.Producer(new MftBlockProduceRequest { Drive = 'T', VolumeSerial = 1, BlockPath = "unused" },
                Token));

        Assert.AreEqual("Drive T: the broker is not running.", thrown.Message);
        Assert.IsNull(source.WatchSource);
    }

    [TestMethod]
    public async Task Unavailable_ReportsTheDriveAsProducerFailedWithTheMessage()
    {
        await using var index = await FileIndex.OpenAsync(_fixture.Options() with { MftSource = MftIndexSources.Unavailable(Reason) }, Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual("Drive T: the broker is not running.", status.Failure?.Message);
    }

    [TestMethod]
    public async Task Unavailable_StillOpensTheCachedBlockAndReportsWatchingNotApplicable()
    {
        await _fixture.Seed();
        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(_fixture.Options() with { MftSource = MftIndexSources.Unavailable(Reason) }, Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], Token));
        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
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

        Assert.AreEqual(1, watchSource.Starts.Count);
        Assert.AreEqual('T', watchSource.Starts[0].Drive);
    }

    [TestMethod]
    public async Task NoMftSource_IsAConfigurationErrorWhenAMftDriveNeedsAScan()
    {
        await using var index = await FileIndex.OpenAsync(_fixture.Options() with { MftSource = null }, Token);
        var failed = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        StringAssert.Contains(failed.Failure?.Message, nameof(FileIndexOptions.MftSource));
    }

    [TestMethod]
    public async Task NoMftSource_IsIgnoredByTheEnumerationPolicy()
    {
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options() with { MftSource = null, Drives = [new IndexedDrive('T', _fixture.Drive().RootDirectory, 0x0BADF00D, ProducerKind.Enumeration)] }, Token);

        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
    }
}
