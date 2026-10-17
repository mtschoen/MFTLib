using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointLossFixture;
using static MFTLib.Tests.TestSupport.JournalWindows;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a consumer sees when a cached block cannot be resumed because the journal no
///     longer holds its checkpoint: the drive is cold-scanned, and its status carries the
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
        await _fixture.Seed();
        using var journal = Journal(firstUsn: CachedNextUsn - 500,
            nextUsn: watchCapable ? CachedNextUsn + 4_000 : CachedNextUsn);

        var options = _fixture.Options(watchSource:
            watchCapable ? new ScriptedWatchSource().IndexSource : null);
        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, drive.Block!.Origin);
        Assert.IsNull(drive.Watch.CheckpointLoss);
    }

    [TestMethod]
    public async Task CheckpointTrimmedAway_RescansAndReportsRoundedSpanPlusMargin()
    {
        await _fixture.Seed();
        // The journal has moved on past the cached checkpoint by 500 bytes, and its tip is
        // 4000 bytes past it.
        using var journal = Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: CachedNextUsn + 4_000);

        await using var reopened = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockOrigin.ProducedByScan, drive.Block!.Origin,
            "an unresumable checkpoint must cold-scan rather than warm-start");
        Assert.AreEqual(DriveState.Ready, drive.State);

        var loss = drive.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.Drive);
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
        await _fixture.Seed();
        using var journal = Journal(journalIdentifier: 0xFEED, firstUsn: 0, nextUsn: 200);

        await using var reopened = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockOrigin.ProducedByScan, drive.Block!.Origin);

        var loss = drive.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    public async Task VolumeThatCannotAnswer_WarmStartsAsBefore()
    {
        await _fixture.Seed();
        using var journal = JournalIsolation.OverrideJournalWindow(_ => null);

        await using var reopened = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, drive.Block!.Origin,
            "a volume that cannot answer is not evidence against the cached block");
        Assert.IsNull(drive.Watch.CheckpointLoss);
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
            Drives = [new IndexedDrive('T', _fixture.Drive().RootDirectory, 0x0BADF00D, ProducerKind.Enumeration)],
            CacheDirectory = _fixture.CacheDirectory
        };
        await using (await FileIndex.OpenAsync(enumerationOptions, CancellationToken.None))
        {
        }

        var checkedDrives = 0;
        using var journal = JournalIsolation.OverrideJournalWindow(_ =>
        {
            checkedDrives++;
            return null;
        });

        await using var reopened = await FileIndex.OpenAsync(enumerationOptions, CancellationToken.None);

        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives.Single().Block!.Origin);
        Assert.IsNull(reopened.Drives.Single().Watch.CheckpointLoss);
        Assert.AreEqual(0, checkedDrives);
    }

    [TestMethod]
    public async Task UnrepresentableRetentionSize_RescansWithNullHintAndClosesCandidate()
    {
        await _fixture.Seed();
        using var journal = Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: long.MaxValue,
            allocationDelta: 1L << 62);
        var scanCount = 0;
        var options = _fixture.Options() with
        {
            MftSource = new MftIndexSource((request, cancellationToken) =>
            {
                using (var exclusive = new FileStream(request.BlockPath,
                           FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.IsTrue(exclusive.Length > 0);
                }

                scanCount++;
                return ProduceMftShapedBlock(request, cancellationToken);
            })
        };

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, scanCount);
        var drive = reopened.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, drive.Block!.Origin);
        var loss = drive.Watch.CheckpointLoss;
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
    ///     age, so it is adopted despite the lost checkpoint rather than failing the drive; the
    ///     size hint that does not fit in a <see cref="long" /> is exactly as absent as it is on
    ///     the non-cache-only path.
    /// </summary>
    [TestMethod]
    public async Task UnrepresentableRetentionSize_CacheOnlyAdoptsTheBlockAndReportsTheLoss()
    {
        await _fixture.Seed();
        using var journal = Journal(
            firstUsn: CachedNextUsn + 500, nextUsn: long.MaxValue,
            allocationDelta: 1L << 62);
        var options = _fixture.Options(
            producer: (_, _) => throw new AssertFailedException("Cache-only must not scan."),
            cacheMode: CacheMode.ExistingOnly);

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, drive.Block!.Origin);
        Assert.IsNull(drive.Failure);
        Assert.IsNotNull(drive.Watch.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, drive.Watch.CheckpointLoss.Cause);
        Assert.IsNull(drive.Watch.CheckpointLoss.SizeThatWouldHaveRetained);
        Assert.IsTrue(reopened.Root('T').IsValid, "the adopted block must still answer queries");
    }

    [TestMethod]
    public async Task CheckpointQueryThrows_PropagatesAndClosesUnpublishedCandidate()
    {
        await _fixture.Seed();
        var failure = new InvalidOperationException("Injected journal query failure.");
        using var journal = JournalIsolation.OverrideJournalWindow(_ => throw failure);

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await using var unexpected = await FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);
        });

        Assert.AreSame(failure, observed);
        var blockPath = Path.Combine(_fixture.CacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0);
    }
}
