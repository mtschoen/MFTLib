using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointLossFixture;
using static MFTLib.Tests.TestSupport.JournalWindows;

namespace MFTLib.Tests.Index;

/// <summary>
///     Who owns a <see cref="JournalCheckpointLoss" /> and how long it lives: a drive that
///     never settles must not leave its report behind for whichever drive reuses its ordinal,
///     a cache-only open that adopts a block despite a lost checkpoint must still say why, and
///     a rescan that replaces the block must drop a report that no longer explains the block
///     in place.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexCheckpointLossLifetimeTests : IDisposable
{
    CheckpointLossFixture _fixture = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new CheckpointLossFixture('T', 'U');

    public void Dispose() => _fixture.Dispose();

    static SyntheticJournalWindow Recreated => Window(0xFEED, 0, 200);

    // --- Ordinal reuse ---

    /// <summary>
    ///     A drive that loses its checkpoint and then fails to scan never adds a block, so the
    ///     next drive takes the same ordinal. The report must go with the drive it describes,
    ///     not with the ordinal.
    /// </summary>
    [TestMethod]
    public async Task FailedDriveKeepsItsOwnLoss_AndTheNextDriveOnThatOrdinalGetsNone()
    {
        var first = _fixture.Drive();
        var second = _fixture.Drive('U');
        await _fixture.Seed(_fixture.Options(drives: [first, second]));

        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow>
        {
            ['T'] = TrimmedWindow,   // T loses its checkpoint
            ['U'] = HealthyWindow    // U can still resume
        });

        // T's cold scan then fails, so T never occupies an ordinal and U reuses it.
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            return request.Drive == 'T'
                ? throw new IOException("synthetic scan failure for T")
                : ProduceMftShapedBlock(request, token);
        }

        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(Producer, drives: [first, second]), CancellationToken.None);

        var failed = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.IsNotNull(failed.Watch.CheckpointLoss, "the failed drive keeps the report that describes it");
        Assert.AreEqual('T', failed.Watch.CheckpointLoss.Drive);

        var settled = index.Drives.Single(drive => drive.Drive == 'U');
        Assert.AreEqual(BlockSource.WarmStartedFromCache, settled.Block!.Source);
        Assert.IsNull(settled.Watch.CheckpointLoss,
            "a drive that reused the failed drive's ordinal must not inherit its report");
    }

    // --- Cache-only ---

    /// <summary>
    ///     The surface that matters most for a cache-only consumer: a cache-only open never
    ///     watches and the block is still a correct snapshot as of its age, so it is adopted
    ///     despite the lost checkpoint, and the status still says why the checkpoint could not
    ///     be resumed and the size a journal would need to be at least to have kept it.
    /// </summary>
    [TestMethod]
    public async Task CacheOnlyAdoptsATrimmedCheckpoint_ReportsWhyAndTheSize()
    {
        var drive = _fixture.Drive();
        await _fixture.Seed(_fixture.Options(drives: [drive]));

        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = TrimmedWindow });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(cacheMode: CacheMode.ExistingOnly, drives: [drive]), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, status.Block!.Source);
        Assert.IsNull(status.Failure);

        var loss = status.Watch.CheckpointLoss;
        Assert.IsNotNull(loss, "a cache-only open must still say why the checkpoint could not be resumed");
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.Drive);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(500L, loss.BytesBehind);
        Assert.AreEqual(4_096L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task CacheOnlyAdoptsOverARecreatedJournal_ReportsTheLossWithNoSize()
    {
        var drive = _fixture.Drive();
        await _fixture.Seed(_fixture.Options(drives: [drive]));

        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = Recreated });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(cacheMode: CacheMode.ExistingOnly, drives: [drive]), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.IsNull(status.Failure);

        var loss = status.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    public async Task CacheOnlyOverAHealthyBlock_WarmStartsAndReportsNoLoss()
    {
        var drive = _fixture.Drive();
        await _fixture.Seed(_fixture.Options(drives: [drive]));

        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(cacheMode: CacheMode.ExistingOnly, drives: [drive]), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(BlockSource.WarmStartedFromCache, status.Block!.Source);
        Assert.IsNull(status.Failure);
        Assert.IsNull(status.Watch.CheckpointLoss);
    }

    // --- Rescan ---

    /// <summary>
    ///     The report explains the block that is in place. A rescan replaces that block, so the
    ///     report stops applying and must not be rendered again after one.
    /// </summary>
    [TestMethod]
    public async Task ASuccessfulRescanClearsTheLoss()
    {
        var drive = _fixture.Drive();
        await _fixture.Seed(_fixture.Options(drives: [drive]));

        using var journals = ForDrives(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = TrimmedWindow });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(drives: [drive]), CancellationToken.None);
        Assert.IsNotNull(index.Drives.Single().Watch.CheckpointLoss);

        await index.RescanAsync(['T'], CancellationToken.None);

        Assert.IsNull(index.Drives.Single().Watch.CheckpointLoss,
            "the rescan replaced the block the report described");
    }
}
