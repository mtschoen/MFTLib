using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointCacheTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A cache-only open adopts a block despite a lost journal checkpoint rather than failing
///     the drive (see <see cref="FileIndexCheckpointLossLifetimeTests" />), because a cache-only
///     open never watches and the block is still a correct snapshot as of its age. But starting a
///     live watch from that block's cursor would resume from a position the journal no longer
///     holds, so <see cref="FileIndex.StartWatchingAsync" /> refuses such
///     a drive and says why, recording the watch as requested the way a start whose source threw
///     does. A successful <see cref="FileIndex.RescanAsync" /> writes a
///     fresh cursor, clears the refusal, and starts the requested watch. The journal
///     read is swapped out through <c>JournalIsolation.OverrideJournalWindow</c>, so these
///     run on every platform and never touch a real volume.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCacheOnlyUnresumableWatchTests
{
    public TestContext TestContext { get; set; } = null!;

    string _firstTreeRoot = null!;
    string _secondTreeRoot = null!;
    string _cacheDirectory = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestInitialize]
    public void Initialize()
    {
        _firstTreeRoot = NewDirectory();
        _secondTreeRoot = NewDirectory();
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
    }

    static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "Documents"));
        File.WriteAllText(Path.Combine(path, "Documents", "readme.md"), "hello");
        return path;
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _firstTreeRoot, _secondTreeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    static IndexedDrive Drive(char letter, string root) =>
        new(letter, root, letter == 'T' ? 0x0BADF00Du : 0x0BADBEEFu);

    FileIndexOptions Options(MftBlockProducer producer, IIndexWatchSource? watchSource, bool cacheOnly,
        params IndexedDrive[] drives)
    {
        return new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = _cacheDirectory,
            MftSource = new MftIndexSource(producer, watchSource),
            CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent
        };
    }

    /// <summary>Writes the drives' cache blocks while the journal still holds every checkpoint.</summary>
    async Task SeedCacheAsync(params IndexedDrive[] drives)
    {
        using var journals = OverrideJournals(drives.ToDictionary(drive => drive.Drive, _ => HealthyWindow));
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, watchSource: null, cacheOnly: false, drives), Token);
        foreach (var drive in index.Drives)
        {
            Assert.AreEqual(BlockSource.ProducedByScan, drive.Block!.Source);
        }
    }

    /// <summary>
    ///     Starts a drive whose block is unresumable and returns the refusal the consumer sees.
    ///     The refusal leaves the watch requested, so a stop or a rescan applies afterwards.
    /// </summary>
    static async Task<InvalidOperationException> RefuseStartAsync(FileIndex index, char driveLetter,
        CancellationToken token)
    {
        var refusal = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.StartWatchingAsync([driveLetter], token));
        StringAssert.Contains(refusal.Message, "RescanAsync");
        return refusal;
    }

    [TestMethod]
    public async Task StartWatchingAsync_RefusesTheUnresumableDriveAndWatchesTheHealthyOne()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var source = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = TrimmedWindow, ['U'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source.IndexSource, cacheOnly: true, driveT, driveU), Token);

        // Both drives are adopted (cache-only never fails a drive for a lost checkpoint alone).
        Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.Drive == 'T').State);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.Drive == 'U').State);
        Assert.IsNotNull(index.Drives.Single(drive => drive.Drive == 'T').Watch.CheckpointLoss);
        Assert.IsNull(index.Drives.Single(drive => drive.Drive == 'U').Watch.CheckpointLoss);

        await RefuseStartAsync(index, 'T', Token);
        await index.StartWatchingAsync(['U'], Token);

        Assert.AreEqual('U', source.Starts.Single().Drive,
            "the drive whose checkpoint could not be resumed must never reach the watch source");

        var unresumable = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.IsNotNull(unresumable.Watch.FailureMessage, "the refusal must be reported, not silent");
        StringAssert.Contains(unresumable.Watch.FailureMessage, "RescanAsync");
        Assert.AreEqual(WatchState.Faulted, unresumable.Watch.State);

        var healthy = index.Drives.Single(drive => drive.Drive == 'U');
        Assert.IsNull(healthy.Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, healthy.Watch.State);

        await source.WatchFor('U').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalIdentifier: CachedJournalIdentifier, NextUsn: 9_500));
        Assert.AreEqual(9_500L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(['U'], Token);
    }

    /// <summary>
    ///     A refusal leaves the watch requested, so the rescan that replaces the block clears the
    ///     refusal and starts the watch from the fresh cursor, exactly as it would after a start
    ///     whose source threw.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDrive_ClearsTheRefusalAndStartsTheRequestedWatch()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var source = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = TrimmedWindow, ['U'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source.IndexSource, cacheOnly: true, driveT, driveU), Token);
        await index.StartWatchingAsync(['U'], Token);
        await RefuseStartAsync(index, 'T', Token);
        Assert.IsTrue(index.Drives.Single(drive => drive.Drive == 'T').Watch.IsRequested,
            "a refused start records the request, as a start whose source threw does");

        await index.RescanAsync(['T'], Token);

        Assert.AreEqual(new IndexWatchTarget('T', CachedJournalIdentifier, CachedNextUsn), source.TargetsFor('T').Single(),
            "the rescan starts the requested watch from the fresh cursor");
        var recovered = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(BlockSource.ProducedByScan, recovered.Block!.Source);
        Assert.IsNull(recovered.Watch.CheckpointLoss, "the rescan replaced the block the report described");
        Assert.IsNull(recovered.Watch.FailureMessage);
        Assert.IsTrue(recovered.Watch.IsRequested);
        Assert.AreEqual(WatchState.CatchingUp,
            index.Drives.Single(drive => drive.Drive == 'T').Watch.State);
        await source.WatchFor('T').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalIdentifier: CachedJournalIdentifier, NextUsn: 5_000));
        Assert.AreEqual(5_000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(['T'], Token);
        await index.StopWatchingAsync(['U'], Token);
    }

    /// <summary>
    ///     A rescan whose producer returns no block replaces nothing and reports a failed result
    ///     carrying <see cref="InvalidOperationException" /> with the producer's failure, and the drive's
    ///     refusal survives exactly as it was: the old, still-unresumable block is not started, and
    ///     the message explaining why is not cleared.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDrive_WhenTheProducerReturnsNoBlock_ReportsFailureAndLeavesTheRefusalIntact()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var scanFailure = new IOException("synthetic scan failure for T");
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            // The producer throws, and the scanning code turns that into a scan with no block.
            return char.ToUpperInvariant((request.Drive).Letter) == 'T'
                ? throw scanFailure
                : ProduceMftShapedBlock(request, token);
        }

        var source = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = TrimmedWindow, ['U'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(Producer, source.IndexSource, cacheOnly: true, driveT, driveU), Token);
        await index.StartWatchingAsync(['U'], Token);
        await RefuseStartAsync(index, 'T', Token);

        var before = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.IsNotNull(before.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, before.Watch.State);
        Assert.IsNotNull(before.Watch.CheckpointLoss);

        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(['T'], Token));

        Assert.AreSame(scanFailure, failure.InnerException);
        Assert.AreEqual(0, source.TargetsFor('T').Count,
            "a failed rescan must not start the cursor the journal still cannot resume");

        var after = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual("synthetic scan failure for T", after.Failure?.Message);
        Assert.IsNotNull(after.Watch.FailureMessage, "the refusal must stay reported after a failed scan");
        StringAssert.Contains(after.Watch.FailureMessage, "RescanAsync");
        Assert.AreEqual(WatchState.Faulted, after.Watch.State);
        Assert.IsNotNull(after.Watch.CheckpointLoss, "the block did not change, so the original loss still explains it");
        Assert.AreEqual(before.Watch.CheckpointLoss, after.Watch.CheckpointLoss);

        // U is unaffected by T's failed rescan.
        await source.WatchFor('U').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "u.txt")], JournalIdentifier: CachedJournalIdentifier, NextUsn: 9_000));
        Assert.AreEqual(9_000L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(['U'], Token);
    }

    /// <summary>
    ///     A scripted producer replaces a cached MFT block with an unwatchable block on rescan.
    ///     The restart defensively withdraws the request
    ///     and faults a wait issued between the retired watch and that restart.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ThatReplacesAWatchedBlockWithAnUnwatchableOne_WithdrawsTheRequestAndFaultsTheWait()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);
        var source = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        BlockFile? producedBlock = null;
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [driveT],
            CacheDirectory = _cacheDirectory,
            MftSource = new MftIndexSource(async (request, cancellationToken) =>
            {
                var result = await ProduceMftShapedBlock(request, cancellationToken);
                producedBlock = result.Block;
                return result;
            }, source.IndexSource)
        }, Token);
        index.PublishInsideWriteGateForTest = _ =>
        {
            if (producedBlock is not null)
            {
                producedBlock.Header.ProducerKind = ProducerKind.Enumeration;
            }
        };
        try
        {
            Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind, "the cached MFT block warm-started");
            await index.StartWatchingAsync(['T'], Token);
            Task<IReadOnlyList<DriveOperationResult>>? wait = null;
            index.BeforeRestartDecisionForTest = _ => wait ??= index.WaitForCatchUpAsync(['T'], CancellationToken.None);

            await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => index.RescanAsync(['T'], Token));

            Assert.IsNotNull(wait);
            var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => wait.WaitAsync(HangGuard));
            StringAssert.Contains(failure.Message, "no MFT-backed block");
            var status = index.Drives.Single();
            Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
            Assert.IsFalse(status.Watch.IsRequested);
            Assert.AreEqual(WatchState.NotStarted, status.Watch.State);
            Assert.AreEqual(1, source.TargetsFor('T').Count);
        }
        finally
        {
            // Disposed explicitly, after the restart seam that captures the index can no longer run.
            await index.DisposeAsync();
        }
    }

    /// <summary>
    ///     The same rescan over a watch that had already faulted on its channel, which the rescan
    ///     retains until the restart: the restart supersedes that faulted watch with its failure,
    ///     so the drive reads exactly as it does after a healthy watch.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ThatReplacesAChannelFaultedWatchsBlockWithAnUnwatchableOne_ReadsNotStarted()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);
        var source = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = HealthyWindow });
        BlockFile? producedBlock = null;
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [driveT],
            CacheDirectory = _cacheDirectory,
            MftSource = new MftIndexSource(async (request, cancellationToken) =>
            {
                var result = await ProduceMftShapedBlock(request, cancellationToken);
                producedBlock = result.Block;
                return result;
            }, source.IndexSource)
        }, Token);
        index.PublishInsideWriteGateForTest = _ =>
        {
            if (producedBlock is not null)
            {
                producedBlock.Header.ProducerKind = ProducerKind.Enumeration;
            }
        };
        var channelFaulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Channel)
            {
                channelFaulted.TrySetResult();
            }
        };
        await index.StartWatchingAsync(['T'], Token);
        source.WatchFor('T').LoseChannel(new IOException("the channel went away"));
        await channelFaulted.Task.WaitAsync(HangGuard);
        Assert.AreEqual(WatchState.Faulted, index.Drives.Single().Watch.State);

        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => index.RescanAsync(['T'], Token));

        var status = index.Drives.Single();
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
        Assert.IsFalse(status.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, status.Watch.State);
        Assert.IsNull(status.Watch.FailureMessage, "the superseded watch's channel failure no longer describes the drive");
        await DriveResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(['T'], Token));
        await DriveResultAssertions.NotApplicableAsync(() => index.StopWatchingAsync(['T'], Token));
        Assert.AreEqual(1, source.TargetsFor('T').Count);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheOnlyMftDriveIsUnresumable_RefusesItAndReportsIt()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);

        var source = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, SyntheticJournalWindow> { ['T'] = TrimmedWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source.IndexSource, cacheOnly: true, driveT), Token);

        var refusal = await RefuseStartAsync(index, 'T', Token);

        Assert.AreEqual(0, source.Starts.Count, "no drive survived to reach the watch source");
        var status = index.Drives.Single();
        Assert.AreEqual(refusal.Message, status.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, status.Watch.State);

        // The refusal is the drive's last start failure, so a catch-up wait faults with it.
        var waitFailure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.WaitForCatchUpAsync(['T'], Token));
        StringAssert.Contains(waitFailure.Message, "RescanAsync");

        // The refusal records the request, so a stop withdraws it without reporting the refusal again,
        // which the start already reported; after that there is nothing left to stop.
        Assert.IsTrue(status.Watch.IsRequested);
        await index.StopWatchingAsync(['T'], Token);
        var stopped = index.Drives.Single();
        Assert.IsFalse(stopped.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, stopped.Watch.State);
        await DriveResultAssertions.NotApplicableAsync(() => index.StopWatchingAsync(['T'], Token));
    }
}
