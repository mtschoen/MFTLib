using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointLossFixture;
using static MFTLib.Tests.TestSupport.JournalWindows;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a consumer sees when a cached block cannot be resumed because the journal no
///     longer holds its checkpoint: the root is cold-scanned, and its status carries the
///     reason and the size a journal would need to be at least to have kept the checkpoint.
///     The journal read is swapped out through <c>JournalIsolation.OverrideJournalWindow</c>,
///     so these run on every platform and never touch a real volume.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexCheckpointLossTests : IDisposable
{
    CheckpointLossFixture _fixture = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new CheckpointLossFixture();

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidCheckpoint_WarmStartsAndReportsNoLoss(bool watchCapable)
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        using var journal = Journal(firstUsn: CachedNextUsn - 500,
            nextUsn: watchCapable ? CachedNextUsn + 4_000 : CachedNextUsn);

        var options = _fixture.Options(watchSource:
            watchCapable ? new ScriptedWatchSource().IndexSource : null);
        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        var root = reopened.Status;
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, root.Block!.Origin);
        Assert.IsNull(root.Watch.CheckpointLoss);
    }

    [TestMethod]
    public async Task CheckpointTrimmedAway_RescansAndReportsRoundedSpanPlusMargin()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        // The journal has moved on past the cached checkpoint by 500 bytes, and its tip is
        // 4000 bytes past it.
        using var journal = Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: CachedNextUsn + 4_000);

        await using var reopened = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

        var root = reopened.Status;
        Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin,
            "an unresumable checkpoint must cold-scan rather than warm-start");
        Assert.AreEqual(RootState.Ready, root.State);

        var loss = root.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.Volume);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(CachedNextUsn + 500, loss.FirstUsn);
        Assert.AreEqual(CachedNextUsn + 4_000, loss.NextUsn);
        Assert.AreEqual(500L, loss.BytesBehind);
        // The 4000-byte span rounds to 4032, then the trimming margin adds one 64-byte delta.
        Assert.AreEqual(4_096L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task JournalRecreated_RescansAndSuggestsNoSize()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        using var journal = Journal(journalIdentifier: 0xFEED, firstUsn: 0, nextUsn: 200);

        await using var reopened = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

        var root = reopened.Status;
        Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin);

        var loss = root.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    public async Task VolumeThatCannotAnswer_WarmStartsAsBefore()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);

        await using var reopened = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

        var root = reopened.Status;
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, root.Block!.Origin,
            "a volume that cannot answer is not evidence against the cached block");
        Assert.IsNull(root.Watch.CheckpointLoss);
    }

    /// <summary>
    ///     An enumeration-backed block carries no journal checkpoint, so nothing about it can
    ///     have fallen out of a journal and the check never runs for it.
    /// </summary>
    [TestMethod]
    public async Task EnumerationBlock_IsNeverCheckedAgainstTheJournal()
    {
        var enumerationOptions = new FileIndexOptions
        {
            Root = SyntheticIndexedRoot.Create(new VolumeKey('T'), _fixture.Root().RootDirectory, 0x0BADF00D, ProducerKind.Enumeration),
            CacheDirectory = _fixture.CacheDirectory
        };
        await using (await FileIndex.OpenAsync(enumerationOptions, CancellationToken.None))
        {
        }

        var checkedRoots = 0;
        using var journal = JournalIsolation.OverrideJournalWindow(_ =>
        {
            checkedRoots++;
            return null;
        });

        await using var reopened = await FileIndex.OpenAsync(enumerationOptions, CancellationToken.None);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block!.Origin);
        Assert.IsNull(reopened.Status.Watch.CheckpointLoss);
        Assert.AreEqual(0, checkedRoots);
    }

    [TestMethod]
    public async Task UnrepresentableRetentionSize_RescansWithNullHintAndClosesCandidate()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        using var journal = Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: long.MaxValue,
            allocationDelta: 1L << 62);
        var scanCount = 0;
        var options = _fixture.Options() with
        {
            MftSource = new MftIndexSource(async (request, cancellationToken) =>
            {
                await using (var exclusive = new FileStream(request.BlockPath,
                           FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.IsGreaterThan(0, exclusive.Length);
                }

                scanCount++;
                return await ProduceMftShapedBlock(request, cancellationToken);
            })
        };

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, scanCount);
        var root = reopened.Status;
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin);
        var loss = root.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(CachedNextUsn + 500, loss.FirstUsn);
        Assert.AreEqual(long.MaxValue, loss.NextUsn);
        Assert.AreEqual(1L << 62, loss.JournalSettings.AllocationDelta);
        Assert.AreEqual(500L, loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    /// <summary>
    ///     A cache-only open never watches and the block is still a correct snapshot as of its
    ///     age, so it is adopted despite the lost checkpoint rather than failing the root; the
    ///     size hint that does not fit in a <see cref="long" /> is exactly as absent as it is on
    ///     the non-cache-only path.
    /// </summary>
    [TestMethod]
    public async Task UnrepresentableRetentionSize_CacheOnlyAdoptsTheBlockAndReportsTheLoss()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        using var journal = Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: long.MaxValue,
            allocationDelta: 1L << 62);
        var options = _fixture.Options(
            producer: (_, _) => throw new AssertFailedException("Cache-only must not scan."),
            cacheMode: CacheMode.ExistingOnly);

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        var root = reopened.Status;
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, root.Block!.Origin);
        Assert.IsNull(root.Failure);
        Assert.IsNotNull(root.Watch.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, root.Watch.CheckpointLoss.Cause);
        Assert.IsNull(root.Watch.CheckpointLoss.SizeThatWouldHaveRetained);
        Assert.IsTrue(reopened.Root(TestContext.CancellationToken).IsValid, "the adopted block must still answer queries");
    }

    [TestMethod]
    public async Task CheckpointQueryThrows_PropagatesAndClosesUnpublishedCandidate()
    {
        await _fixture.Seed(cancellationToken: TestContext.CancellationToken);
        var failure = new InvalidOperationException("Injected journal query failure.");
        using var journal = JournalIsolation.OverrideJournalWindow(_ => throw failure);

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await using var unexpected = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);
        });

        Assert.AreSame(failure, observed);
        var blockPath = Path.Combine(_fixture.CacheDirectory, CacheDirectory.BlockFileName(new VolumeKey('T'), 0x0BADF00D));
        await using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0, exclusive.Length);
    }

    public TestContext TestContext { get; set; } = null!;
}
